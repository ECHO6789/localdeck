using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LocalDeck.App.Services;

public sealed class UninstallerService
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledApp> GetInstalledApps()
    {
        var apps = new List<InstalledApp>();
        foreach (var (hive, hiveName) in new[]
                 {
                     (RegistryHive.CurrentUser, "HKCU"),
                     (RegistryHive.LocalMachine, "HKLM")
                 })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(UninstallPath);
                    if (uninstall is null)
                    {
                        continue;
                    }
                    var registrySubPath = view == RegistryView.Registry32
                        ? @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                        : UninstallPath;

                    foreach (var subName in uninstall.GetSubKeyNames())
                    {
                        using var key = uninstall.OpenSubKey(subName);
                        if (key is null)
                        {
                            continue;
                        }

                        var name = key.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name) ||
                            Convert.ToInt32(key.GetValue("SystemComponent") ?? 0) == 1 ||
                            key.GetValue("ParentKeyName") is not null)
                        {
                            continue;
                        }

                        var uninstallString = (key.GetValue("QuietUninstallString") as string) ??
                                              (key.GetValue("UninstallString") as string) ??
                                              "";
                        if (string.IsNullOrWhiteSpace(uninstallString))
                        {
                            continue;
                        }

                        var installLocation = key.GetValue("InstallLocation") as string ?? "";
                        var estimatedKb = long.TryParse(
                            key.GetValue("EstimatedSize")?.ToString(), out var size) ? size : 0;
                        apps.Add(new InstalledApp(
                            $"{hiveName}\\{registrySubPath}\\{subName}",
                            name,
                            key.GetValue("DisplayVersion") as string ?? "",
                            key.GetValue("Publisher") as string ?? "",
                            uninstallString,
                            installLocation,
                            estimatedKb * 1024,
                            key.GetValue("InstallDate") as string ?? "",
                            hiveName,
                            view == RegistryView.Registry64 ? "64" : "32",
                            Directory.Exists(installLocation)));
                    }
                }
                catch (Exception ex)
                {
                    AppPaths.Log($"Uninstall registry {hiveName}/{view} failed: {ex.Message}");
                }
            }
        }

        return apps
            .DistinctBy(app => $"{app.Name}|{app.Version}|{app.RegistryPath}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public bool LaunchUninstaller(InstalledApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c {app.UninstallString}")
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Uninstall launch failed for {app.Name}: {ex.Message}");
            return false;
        }
    }

    public IReadOnlyList<AppLeftover> ScanLeftovers(InstalledApp app)
    {
        var result = new List<AppLeftover>();
        var tokens = NameTokens(app.Name, app.Publisher);
        foreach (var directory in CandidateDirectories(tokens))
        {
            if (Directory.Exists(directory))
            {
                result.Add(new AppLeftover(
                    "directory", directory, "可能是卸载后残留目录", true, false, directory));
            }
        }

        foreach (var leftover in GetInstalledApps()
                     .Where(candidate =>
                         candidate.RegistryPath != app.RegistryPath &&
                         !candidate.HasValidInstallLocation &&
                         NameMatches(candidate.Name, tokens))
                     .Take(20))
        {
            result.Add(new AppLeftover(
                "registry",
                leftover.RegistryPath,
                $"损坏的卸载登记：{leftover.Name}",
                false,
                true,
                null));
        }

        return result.DistinctBy(item => $"{item.Kind}|{item.Target}", StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string QuarantineDirectory(AppLeftover leftover)
    {
        if (leftover.Kind != "directory" || string.IsNullOrWhiteSpace(leftover.DirectoryPath) ||
            !Directory.Exists(leftover.DirectoryPath))
        {
            throw new InvalidOperationException("Directory does not exist.");
        }

        var quarantineRoot = Path.Combine(
            AppPaths.DataDirectory, "quarantine", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(quarantineRoot);
        var target = Path.Combine(quarantineRoot, Path.GetFileName(leftover.DirectoryPath.TrimEnd('\\')));
        Directory.Move(leftover.DirectoryPath, target);
        return target;
    }

    public string DeleteRegistryWithBackup(AppLeftover leftover)
    {
        if (leftover.Kind != "registry" || string.IsNullOrWhiteSpace(leftover.DirectoryPath) &&
            string.IsNullOrWhiteSpace(leftover.Target))
        {
            throw new InvalidOperationException("Invalid registry leftover.");
        }

        var registryPath = leftover.Target;
        if (!registryPath.Contains(@"\Uninstall\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only uninstall registry keys can be removed.");
        }
        var fullRegistryPath = registryPath.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
            ? @"HKEY_CURRENT_USER\" + registryPath[5..]
            : registryPath.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)
                ? @"HKEY_LOCAL_MACHINE\" + registryPath[5..]
                : registryPath;

        var backupDirectory = Path.Combine(AppPaths.DataDirectory, "registry-backups");
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(
            backupDirectory,
            $"{SanitizeFileName(registryPath)}-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
        var export = Process.Start(new ProcessStartInfo(
            "reg.exe", $"export \"{fullRegistryPath}\" \"{backup}\" /y")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        export?.WaitForExit(5000);
        if (export is null || export.ExitCode != 0 || !File.Exists(backup))
        {
            throw new InvalidOperationException("Registry backup failed; key was not removed.");
        }

        var roots = new[]
        {
            (Prefix: @"HKEY_CURRENT_USER", Hive: RegistryHive.CurrentUser),
            (Prefix: @"HKEY_LOCAL_MACHINE", Hive: RegistryHive.LocalMachine)
        };
        var root = roots.FirstOrDefault(item =>
            fullRegistryPath.StartsWith(item.Prefix, StringComparison.OrdinalIgnoreCase));
        if (root.Prefix is null)
        {
            throw new InvalidOperationException("Unsupported registry hive.");
        }

        var relative = fullRegistryPath[(root.Prefix.Length + 1)..];
        var parentEnd = relative.LastIndexOf('\\');
        var parentPath = parentEnd > 0 ? relative[..parentEnd] : "";
        var keyName = parentEnd >= 0 ? relative[(parentEnd + 1)..] : relative;
        using var baseKey = RegistryKey.OpenBaseKey(root.Hive, RegistryView.Registry64);
        using var parent = baseKey.OpenSubKey(parentPath, writable: true);
        parent?.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
        return backup;
    }

    private static IEnumerable<string> CandidateDirectories(IReadOnlySet<string> tokens)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(root);
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                if (NameMatches(Path.GetFileName(directory), tokens))
                {
                    yield return directory;
                }
            }
        }
    }

    private static HashSet<string> NameTokens(string name, string publisher)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "inc", "ltd", "llc", "corp", "corporation", "limited", "software",
            "technology", "technologies", "the", "and", "for", "app", "pro"
        };
        return Regex.Split($"{name} {publisher}", @"[^A-Za-z0-9\u4e00-\u9fff]+")
            .Select(token => token.Trim().ToLowerInvariant())
            .Where(token => token.Length >= 4 && !ignored.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool NameMatches(string candidate, IReadOnlySet<string> tokens)
    {
        var normalized = candidate.ToLowerInvariant();
        return tokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
    }

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}

public sealed record InstalledApp(
    string RegistryPath,
    string Name,
    string Version,
    string Publisher,
    string UninstallString,
    string InstallLocation,
    long EstimatedBytes,
    string InstallDate,
    string Scope,
    string RegistryView,
    bool HasValidInstallLocation);

public sealed record AppLeftover(
    string Kind,
    string Target,
    string Description,
    bool IsDirectory,
    bool IsRegistry,
    string? DirectoryPath);
