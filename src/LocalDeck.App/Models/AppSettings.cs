namespace LocalDeck.App.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; }
    public List<string> SearchRoots { get; set; } = [];
    public string? ClashConfigPath { get; set; }
    public string? ProxyProfilePath { get; set; }
    public int ProxyPort { get; set; } = 7897;
    public string ProxyMode { get; set; } = "rule";
    public string? ProxySelectedNode { get; set; }
    public bool ProxyAutoStart { get; set; } = true;
    public bool ProxySystemProxyEnabled { get; set; }
    public string? SelectedLogitechDevice { get; set; }
    public int PointerSpeed { get; set; } = 10;
    public bool ClipboardCaptureImages { get; set; } = true;
    public int ClipboardHistoryLimit { get; set; } = 500;
    public bool MinimizeToTray { get; set; } = true;
    public bool LaunchAtLogin { get; set; }
    public bool MouseMacrosEnabled { get; set; } = true;
    public List<string> EnabledPlugins { get; set; } = [];
    public bool PluginOnboardingCompleted { get; set; }
    public bool PerformanceOverlayEnabled { get; set; } = true;
    public double? PerformanceOverlayLeft { get; set; }
    public double? PerformanceOverlayTop { get; set; }
    public double PerformanceOverlayWidth { get; set; } = 310;
    public double PerformanceOverlayHeight { get; set; } = 340;
    public double PerformanceOverlayOpacity { get; set; } = 0.9;
    public string PerformanceOverlayFpsSource { get; set; } = "foreground";
    public List<string> PerformanceOverlayMetrics { get; set; } =
    [
        "cpuUsage", "cpuTemp", "gpuUsage", "gpuTemp", "gpuMemory",
        "fps", "bluetooth"
    ];
    public string? LocalModelDirectory { get; set; }
    public string? LocalModelEngineDirectory { get; set; }
    public int LocalModelPort { get; set; } = 8686;
    public Dictionary<string, string> LocalModelArguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> MacroBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MouseX1"] = string.Empty,
        ["MouseX2"] = string.Empty
    };
    public List<MacroDefinition> Macros { get; set; } = [];
    public HotkeySettings Hotkeys { get; set; } = new();
}

public sealed record PerformanceOverlaySettingsUpdate(
    bool? Enabled,
    double? Opacity,
    string? FpsSource,
    List<string>? Metrics,
    double? Width,
    double? Height);

public sealed class HotkeySettings
{
    public string Clipboard { get; set; } = "Ctrl+Alt+V";
    public string Screenshot { get; set; } = "F1";
    public string ScreenshotPin { get; set; } = "Ctrl+Alt+S";
    public string PinImage { get; set; } = "F3";
    public string Search { get; set; } = "Ctrl+Alt+D";
    public string OpenPanel { get; set; } = "Ctrl+Alt+Space";
    public string ProxyToggle { get; set; } = "Ctrl+Alt+G";
    public string MacroEditor { get; set; } = "Ctrl+Alt+M";
    public string Launcher { get; set; } = "Ctrl+Alt+E";
    public string Overlay { get; set; } = "Ctrl+Alt+P";
    public List<CustomHotkey> Custom { get; set; } = [];
}

public sealed class CustomHotkey
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "新快捷键";
    public string Combo { get; set; } = "";
    public string Action { get; set; } = "showPanel";
    public string Arguments { get; set; } = "";
}

public sealed class MacroDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新宏";
    public bool Enabled { get; set; } = true;
    public List<MacroStep> Steps { get; set; } = [];
}

public sealed class MacroStep
{
    public string Type { get; set; } = "key";
    public string Value { get; set; } = "";
    public int DelayMs { get; set; } = 40;
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed record SystemSnapshot(
    double CpuPercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    int? BatteryPercent,
    bool BatteryCharging,
    string Uptime,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    IReadOnlyList<DiskSnapshot> Disks,
    IReadOnlyList<ProcessSnapshot> TopProcesses);

public sealed record DiskSnapshot(string Name, string Label, long TotalBytes, long FreeBytes);

public sealed record ProcessSnapshot(int Id, string Name, long WorkingSetBytes, double CpuSeconds);
