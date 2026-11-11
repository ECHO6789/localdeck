using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using LocalDeck.App.Models;

namespace LocalDeck.App.Services;

public sealed class SettingsService
{
    public event Action<AppSettings>? Changed;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        AppPaths.EnsureCreated();

        if (File.Exists(AppPaths.SettingsPath))
        {
            try
            {
                Current = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(AppPaths.SettingsPath), JsonOptions) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Settings load failed: {ex.Message}");
                Current = new AppSettings();
            }
        }

        if (Current.SearchRoots.Count == 0)
        {
            Current.SearchRoots = DefaultSearchRoots();
        }

        Current.ClashConfigPath ??= FindClashConfig();
        if (Current.SchemaVersion < 3)
        {
            Current.ProxyProfilePath ??= ImportExistingClashProfile(Current.ClashConfigPath);
            Current.Hotkeys.Screenshot = "F1";
            Current.Hotkeys.PinImage = "F3";
            Current.Hotkeys.OpenPanel = "Ctrl+Alt+Space";
            Current.SchemaVersion = 3;
        }
        if (Current.SchemaVersion < 4)
        {
            Current.Hotkeys.Search = "Ctrl+Alt+D";
            Current.Hotkeys.ProxyToggle = "Ctrl+Alt+G";
            Current.SchemaVersion = 4;
        }
        if (Current.SchemaVersion < 5)
        {
            Current.Hotkeys.MacroEditor = "Ctrl+Alt+M";
            Current.LaunchAtLogin = true;
            Current.SchemaVersion = 5;
        }
        if (Current.SchemaVersion < 6)
        {
            Current.Hotkeys.Launcher = "Ctrl+Alt+L";
            Current.SchemaVersion = 6;
        }
        if (Current.SchemaVersion < 7)
        {
            if (string.IsNullOrWhiteSpace(Current.Hotkeys.Launcher) ||
                Current.Hotkeys.Launcher.Equals("Ctrl+Alt+L", StringComparison.OrdinalIgnoreCase))
            {
                Current.Hotkeys.Launcher = "Ctrl+Alt+E";
            }
            Current.SchemaVersion = 7;
        }
        if (Current.SchemaVersion < 8)
        {
            Current.ProxyAutoStart = true;
            Current.ProxySystemProxyEnabled = true;
            Current.SchemaVersion = 8;
        }
        if (Current.SchemaVersion < 9)
        {
            Current.ProxyAutoStart = true;
            Current.ProxySystemProxyEnabled = true;
            Current.SchemaVersion = 9;
        }
        if (Current.SchemaVersion < 10)
        {
            Current.PerformanceOverlayEnabled = true;
            Current.Hotkeys.Overlay = "Ctrl+Alt+F";
            Current.SchemaVersion = 10;
        }
        if (Current.SchemaVersion < 11)
        {
            if (string.IsNullOrWhiteSpace(Current.Hotkeys.Overlay) ||
                Current.Hotkeys.Overlay.Equals("Ctrl+Alt+F", StringComparison.OrdinalIgnoreCase))
            {
                Current.Hotkeys.Overlay = "Ctrl+Alt+P";
            }
            Current.SchemaVersion = 11;
        }
        if (Current.SchemaVersion < 12)
        {
            Current.PerformanceOverlayOpacity = Math.Clamp(
                Current.PerformanceOverlayOpacity, 0.3, 1);
            Current.PerformanceOverlayFpsSource ??= "foreground";
            Current.PerformanceOverlayMetrics ??=
            [
                "cpuUsage", "cpuTemp", "gpuUsage", "gpuTemp", "gpuMemory",
                "fps", "bluetooth"
            ];
            Current.SchemaVersion = 12;
        }
        if (Current.SchemaVersion < 13)
        {
            Current.PerformanceOverlayWidth = Current.PerformanceOverlayWidth <= 0
                ? 310
                : Math.Clamp(Current.PerformanceOverlayWidth, 220, 800);
            Current.PerformanceOverlayHeight = Current.PerformanceOverlayHeight <= 0
                ? 340
                : Math.Clamp(Current.PerformanceOverlayHeight, 180, 900);
            Current.SchemaVersion = 13;
        }
        if (Current.SchemaVersion < 14)
        {
            Current.ProxyAutoStart = true;
            Current.ProxySystemProxyEnabled = false;
            Current.SchemaVersion = 14;
        }
        if (Current.SchemaVersion < 15)
        {
            Current.EnabledPlugins ??= [];
            Current.PluginOnboardingCompleted = false;
            Current.SchemaVersion = 15;
        }
        Current.ProxyProfilePath ??= FindExistingProfile();
        if (string.IsNullOrWhiteSpace(Current.ProxyProfilePath) ||
            !LooksLikeProxyProfile(Current.ProxyProfilePath))
        {
            Current.ProxyProfilePath = ImportExistingClashProfile(Current.ClashConfigPath)
                ?? FindExistingProfile();
        }
        Current.Hotkeys.Custom ??= [];
        Current.Macros ??= [];
        Current.EnabledPlugins ??= [];
        Current.PerformanceOverlayMetrics ??= [];
        Current.PerformanceOverlayFpsSource ??= "foreground";
        Current.PerformanceOverlayOpacity = Math.Clamp(
            Current.PerformanceOverlayOpacity, 0.3, 1);
        Current.PerformanceOverlayWidth = Math.Clamp(
            Current.PerformanceOverlayWidth, 220, 800);
        Current.PerformanceOverlayHeight = Math.Clamp(
            Current.PerformanceOverlayHeight, 180, 900);
        Current.LocalModelArguments ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Current.LocalModelDirectory ??= FindLocalModelDirectory();
        Current.LocalModelEngineDirectory ??= FindLlamaEngineDirectory();
        Current.MacroBindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MouseX1"] = string.Empty,
            ["MouseX2"] = string.Empty
        };
        Save();
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        using var processLock = new Mutex(false, @"Local\LocalDeck.Settings");
        var ownsMutex = false;
        var temporary = $"{AppPaths.SettingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            ownsMutex = processLock.WaitOne(TimeSpan.FromSeconds(5));
            if (!ownsMutex)
            {
                throw new IOException("Timed out waiting for the settings file lock.");
            }
            File.WriteAllText(temporary, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temporary, AppPaths.SettingsPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            if (ownsMutex)
            {
                processLock.ReleaseMutex();
            }
        }
    }

    public void Update(AppSettings settings)
    {
        Current = settings;
        Current.SearchRoots = settings.SearchRoots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Current.ProxyPort = Math.Clamp(settings.ProxyPort, 1024, 65535);
        Current.ProxyMode = settings.ProxyMode is "rule" or "global" or "direct"
            ? settings.ProxyMode
            : "rule";
        Current.Hotkeys.Custom ??= [];
        Current.Macros ??= [];
        Current.MacroBindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Current.PerformanceOverlayMetrics ??= [];
        Current.PerformanceOverlayFpsSource ??= "foreground";
        Current.PerformanceOverlayOpacity = Math.Clamp(
            settings.PerformanceOverlayOpacity, 0.3, 1);
        Current.PerformanceOverlayWidth = Math.Clamp(
            settings.PerformanceOverlayWidth, 220, 800);
        Current.PerformanceOverlayHeight = Math.Clamp(
            settings.PerformanceOverlayHeight, 180, 900);
        foreach (var macro in Current.Macros)
        {
            foreach (var step in macro.Steps)
            {
                step.DelayMs = Math.Clamp(step.DelayMs, 0, 10_000);
            }
        }
        Save();
        Changed?.Invoke(Current);
    }

    private static List<string> DefaultSearchRoots()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

        return candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindClashConfig()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(roaming, "io.github.clash-verge-rev.clash-verge-rev", "verge.yaml"),
            Path.Combine(roaming, "io.github.clash-verge-rev.clash-verge-rev", "config.yaml"),
            Path.Combine(roaming, "clash-verge", "verge.yaml")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ImportExistingClashProfile(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            return null;
        }

        try
        {
            var profileSource = FindActiveClashProfile(source) ?? source;
            if (!LooksLikeProxyProfile(profileSource))
            {
                return null;
            }

            var target = Path.Combine(AppPaths.ProfileDirectory, "imported-clash.yaml");
            File.Copy(profileSource, target, overwrite: true);
            return target;
        }
        catch
        {
            return source;
        }
    }

    private static string? FindExistingProfile()
    {
        var imported = Path.Combine(AppPaths.ProfileDirectory, "imported-clash.yaml");
        return File.Exists(imported) ? imported : null;
    }

    private static string? FindActiveClashProfile(string source)
    {
        var directory = Path.GetDirectoryName(source);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var metadata = Path.Combine(directory, "profiles.yaml");
        if (File.Exists(metadata))
        {
            try
            {
                var text = File.ReadAllText(metadata);
                var current = Regex.Match(text, @"(?m)^\s*current:\s*(?<uid>[^\r\n#]+)")
                    .Groups["uid"].Value.Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(current))
                {
                    var escaped = Regex.Escape(current);
                    var item = Regex.Match(
                        text,
                        $@"(?ms)^\s*-\s*uid:\s*{escaped}\s*.*?^\s*file:\s*(?<file>[^\r\n#]+)");
                    var file = item.Groups["file"].Value.Trim().Trim('"', '\'');
                    var candidate = Path.Combine(directory, "profiles", file);
                    if (File.Exists(candidate) && LooksLikeProxyProfile(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // Fall through to scanning local profile files.
            }
        }

        var profilesDirectory = Path.Combine(directory, "profiles");
        return Directory.Exists(profilesDirectory)
            ? Directory.GetFiles(profilesDirectory, "*.yaml")
                .Where(file => !Path.GetFileName(file).Equals("Merge.yaml", StringComparison.OrdinalIgnoreCase))
                .Where(LooksLikeProxyProfile)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    private static bool LooksLikeProxyProfile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var reader = new StreamReader(path);
            var buffer = new char[128 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).Contains("\nproxies:", StringComparison.Ordinal) ||
                   new string(buffer, 0, read).StartsWith("proxies:", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindLocalModelDirectory()
    {
        var locModelSettings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocModel", "settings.json");
        try
        {
            if (File.Exists(locModelSettings))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(locModelSettings));
                if (document.RootElement.TryGetProperty("models_dir", out var models) &&
                    models.GetString() is { } modelPath &&
                    Directory.Exists(modelPath))
                {
                    return modelPath;
                }
            }
        }
        catch
        {
            // Fall through to LocalDeck's private model directory.
        }

        return Path.Combine(AppPaths.DataDirectory, "models");
    }

    private static string? FindLlamaEngineDirectory()
    {
        var engines = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocModel", "engines");
        if (!Directory.Exists(engines))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(engines, "b*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "llama-server.exe")));
        }
        catch
        {
            return null;
        }
    }
}
