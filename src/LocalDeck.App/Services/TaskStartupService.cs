using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LocalDeck.App.Services;

public sealed class TaskStartupService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string FolderApprovedPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public IReadOnlyList<StartupItem> GetStartupItems()
    {
        var items = new List<StartupItem>();
        AddRunItems(items, Registry.CurrentUser, "HKCU", RunPath, "Run");
        AddRunItems(items, Registry.CurrentUser, "HKCU", RunOncePath, "RunOnce");
        AddRunItems(items, Registry.LocalMachine, "HKLM", RunPath, "Run");
        AddRunItems(items, Registry.LocalMachine, "HKLM", RunOncePath, "RunOnce");
        AddStartupFolders(items);
        return items
            .OrderByDescending(item => item.Enabled)
            .ThenBy(item => item.Name)
            .ToList();
    }

    public bool SetStartupEnabled(StartupItem item, bool enabled)
    {
        try
        {
            var approvedRoot = item.Scope == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            using var approved = approvedRoot.CreateSubKey(
                item.Kind == "StartupFolder" ? FolderApprovedPath : $@"{ApprovedPath}\{item.Kind}",
                writable: true);
            if (approved is null)
            {
                return false;
            }

            var value = new byte[12];
            value[0] = enabled ? (byte)2 : (byte)3;
            approved.SetValue(item.Name, value, RegistryValueKind.Binary);
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Startup toggle failed: {ex.Message}");
            return false;
        }
    }

    public StartupCleanupResult CleanupOrphanedStartupItems()
    {
        var removed = new List<string>();
        var failed = new List<string>();
        foreach (var item in GetStartupItems())
        {
            if (item.Name.Equals("LocalDeck", StringComparison.OrdinalIgnoreCase) ||
                !IsOrphaned(item))
            {
                continue;
            }

            try
            {
                if (item.Kind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(item.Command))
                    {
                        File.Delete(item.Command);
                    }
                }
                else
                {
                    var root = item.Scope == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                    var path = item.Kind.Equals("RunOnce", StringComparison.OrdinalIgnoreCase)
                        ? RunOncePath
                        : RunPath;
                    using var key = root.OpenSubKey(path, writable: true);
                    key?.DeleteValue(item.Name, throwOnMissingValue: false);
                    using var approved = root.OpenSubKey(
                        item.Kind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase)
                            ? FolderApprovedPath
                            : $@"{ApprovedPath}\{item.Kind}",
                        writable: true);
                    approved?.DeleteValue(item.Name, throwOnMissingValue: false);
                }
                removed.Add(item.Name);
            }
            catch (Exception ex)
            {
                failed.Add(item.Name);
                AppPaths.Log($"Startup cleanup failed for {item.Name}: {ex.Message}");
            }
        }

        return new StartupCleanupResult(removed, failed);
    }

    public IReadOnlyList<ScheduledTaskInfo> GetScheduledTasks()
    {
        var result = new List<ScheduledTaskInfo>();
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Task Scheduler COM service is unavailable.");
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            dynamic root = service.GetFolder("\\");
            EnumerateTaskFolder(root, result, 0);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Scheduled task enumeration failed: {ex.Message}");
        }
        return result.OrderBy(item => item.Path).ToList();
    }

    public bool SetScheduledTaskEnabled(ScheduledTaskInfo item, bool enabled)
    {
        try
        {
            var task = FindTask(item.Path);
            if (task is null)
            {
                return false;
            }
            task.Enabled = enabled;
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Task toggle failed: {ex.Message}");
            return false;
        }
    }

    public bool RunScheduledTask(ScheduledTaskInfo item)
    {
        try
        {
            return FindTask(item.Path) is { } task && RunTask(task);
        }
        catch
        {
            return false;
        }
    }

    public bool StopScheduledTask(ScheduledTaskInfo item)
    {
        try
        {
            return FindTask(item.Path) is { } task && StopTask(task);
        }
        catch
        {
            return false;
        }
    }

    private static void AddRunItems(
        List<StartupItem> items,
        RegistryKey root,
        string scope,
        string path,
        string kind)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null)
            {
                return;
            }

            using var approved = root.OpenSubKey($@"{ApprovedPath}\{kind}");
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                items.Add(new StartupItem(
                    $"{scope}|{kind}|{name}",
                    name,
                    scope,
                    kind,
                    key.GetValue(name)?.ToString() ?? "",
                    IsApprovedEnabled(approved, name),
                    true));
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Startup registry {scope}\\{kind} unavailable: {ex.Message}");
        }
    }

    private static void AddStartupFolders(List<StartupItem> items)
    {
        var folders = new[]
        {
            (Scope: "HKCU", Path: Environment.GetFolderPath(Environment.SpecialFolder.Startup)),
            (Scope: "HKLM", Path: Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup))
        };

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder.Path))
            {
                continue;
            }

            using var approved = (folder.Scope == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser)
                .OpenSubKey(FolderApprovedPath);
            foreach (var file in Directory.EnumerateFiles(folder.Path))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                items.Add(new StartupItem(
                    $"{folder.Scope}|StartupFolder|{name}",
                    name,
                    folder.Scope,
                    "StartupFolder",
                    file,
                    IsApprovedEnabled(approved, name),
                    true));
            }
        }
    }

    private static bool IsApprovedEnabled(RegistryKey? approved, string name)
    {
        var value = approved?.GetValue(name) as byte[];
        return value is not { Length: > 0 } || value[0] != 3;
    }

    private static bool IsOrphaned(StartupItem item)
    {
        try
        {
            if (item.Kind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase))
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type is null)
                {
                    return false;
                }
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic shortcut = shell.CreateShortcut(item.Command);
                var target = Convert.ToString(shortcut.TargetPath) ?? "";
                return !string.IsNullOrWhiteSpace(target) &&
                       !File.Exists(Environment.ExpandEnvironmentVariables(target));
            }

            var command = Environment.ExpandEnvironmentVariables(item.Command).Trim();
            var executable = FirstExecutableToken(command);
            return !string.IsNullOrWhiteSpace(executable) &&
                   !executable.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
                   !File.Exists(executable);
        }
        catch
        {
            return false;
        }
    }

    private static string FirstExecutableToken(string command)
    {
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : "";
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            command,
            @"^(?<path>.*?\.exe)(?:\s|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["path"].Value : "";
    }

    private static void EnumerateTaskFolder(
        dynamic folder, List<ScheduledTaskInfo> result, int depth)
    {
        if (depth > 5)
        {
            return;
        }

        try
        {
            foreach (dynamic task in folder.GetTasks(1))
            {
                try
                {
                    var state = Convert.ToInt32(task.State);
                    result.Add(new ScheduledTaskInfo(
                        (string)task.Path,
                        (string)task.Name,
                        state switch
                        {
                            1 => "已禁用",
                            2 => "排队中",
                            3 => "就绪",
                            4 => "运行中",
                            _ => "未知"
                        },
                        Convert.ToDateTime(task.LastRunTime),
                        Convert.ToDateTime(task.NextRunTime),
                        Convert.ToInt32(task.LastTaskResult),
                        state != 1));
                }
                catch
                {
                    // Some protected system tasks cannot be read completely.
                }
            }

            foreach (dynamic child in folder.GetFolders(0))
            {
                EnumerateTaskFolder(child, result, depth + 1);
            }
        }
        catch
        {
            // Skip protected task folders.
        }
    }

    private static dynamic? FindTask(string path)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Task Scheduler COM service is unavailable.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        var normalized = path.TrimEnd('\\');
        var separator = normalized.LastIndexOf('\\');
        var folderPath = separator <= 0 ? "\\" : normalized[..separator];
        var name = normalized[(separator + 1)..];
        dynamic folder = service.GetFolder(folderPath);
        return folder.GetTask(name);
    }

    private static bool RunTask(dynamic task)
    {
        task.Run(null);
        return true;
    }

    private static bool StopTask(dynamic task)
    {
        task.Stop(null);
        return true;
    }
}

public sealed record StartupItem(
    string Id,
    string Name,
    string Scope,
    string Kind,
    string Command,
    bool Enabled,
    bool CanToggle);

public sealed record ScheduledTaskInfo(
    string Path,
    string Name,
    string State,
    DateTime LastRun,
    DateTime NextRun,
    int LastResult,
    bool Enabled);

public sealed record StartupCleanupResult(
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Failed);
