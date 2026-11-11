namespace LocalDeck.App.Services;

public sealed class PluginCatalogService
{
    private static readonly PluginPackDefinition[] Definitions =
    [
        new(
            "hardware",
            "硬件控制包",
            "显示器亮度、Logitech HID++、设备管理器、厂商风扇/功率、驱动与 GPU 调校。",
            ["performance", "gpu-unlock", "devices", "device-manager", "drivers"],
            "bundled",
            ["自动识别主板", "DDC/CI 与 WMI 亮度", "NVIDIA NVML", "可选厂商本地接口"]),
        new(
            "system-tools",
            "系统维护包",
            "卸载残留清理、磁盘占用分析、Windows 日志与 minidump 分析。",
            ["uninstaller", "disk", "logs"],
            "bundled",
            ["注册表卸载项", "余额隔离", "目录占用扫描", "事件日志"]),
        new(
            "automation",
            "自动化包",
            "侧键宏、宏录制、计划任务和开机启动项管理。",
            ["macros", "tasks"],
            "bundled",
            ["低级鼠标钩子", "宏录制", "Task Scheduler", "HKCU/HKLM Run"]),
        new(
            "ai-workbench",
            "AI 工作台",
            "本地 GGUF 模型、llama.cpp 引擎、TPS 估算以及云端模型快捷入口。",
            ["models"],
            "on-demand",
            ["本地模型扫描", "按需下载", "llama.cpp 服务", "云端模型链接"]),
        new(
            "game-enhancements",
            "游戏增强包",
            "GPU Unlock 兼容检查与 DLSS Unlocked 游戏目录部署。",
            ["gpu-unlock"],
            "on-demand",
            ["NVIDIA PCI 指纹", "Profile 门控", "DLSS 包校验", "游戏文件回滚"])
    ];

    private readonly SettingsService _settings;
    private readonly HardwareProfileService _hardware;
    private readonly GpuControlService _gpu;
    private readonly object _sync = new();
    private PluginCatalogSnapshot? _cached;
    private DateTime _cachedAt = DateTime.MinValue;

    public PluginCatalogService(
        SettingsService settings,
        HardwareProfileService hardware,
        GpuControlService gpu)
    {
        _settings = settings;
        _hardware = hardware;
        _gpu = gpu;
    }

    public PluginCatalogSnapshot GetCatalog(bool refresh = false)
    {
        lock (_sync)
        {
            if (!refresh && _cached is not null &&
                DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(20))
            {
                return _cached;
            }

            var board = _hardware.Read();
            var gpu = _gpu.Read(force: refresh);
            var displayCount = ReadDisplayCount();
            var logitechCount = ReadLogitechCount();
            var engineReady = HasLocalModelEngine();
            var enabled = _settings.Current.EnabledPlugins
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var capabilities = new List<FeatureCapability>
            {
                new("operating-system", "Windows 桌面能力", "supported",
                    $"Windows {Environment.OSVersion.Version}"),
                new("webview2", "内嵌界面", "supported", "LocalDeck 正在 WebView2 中运行"),
                new("clipboard", "剪贴板与截图", "supported", "本地 DPAPI 与 Win32 截图"),
                new("file-search", "文件搜索", "supported", "SQLite FTS5 本地索引"),
                new("traffic-forwarding", "流量转发", "supported", "本机 HTTP CONNECT / SOCKS5"),
                new("display-control", "显示器亮度", displayCount > 0 ? "supported" : "unavailable",
                    displayCount > 0 ? $"检测到 {displayCount} 个可枚举显示目标" : "未发现活动显示目标"),
                new("board-control", "主板厂商控制", board.ControlAvailable ? "supported" : "limited",
                    board.ControlAvailable
                        ? $"{board.FamilyName} / {board.PanelName}"
                        : $"{board.FamilyName} 已识别；厂商写入接口未开放"),
                new("nvidia", "NVIDIA GPU", gpu.Available ? "supported" : "unavailable",
                    gpu.Available
                        ? $"{gpu.Name} · NVML 功耗/频率可读"
                        : gpu.Error ?? "未发现 NVML GPU"),
                new("logitech", "Logitech HID++", logitechCount > 0 ? "supported" : "unavailable",
                    logitechCount > 0 ? $"{logitechCount} 个直连设备" : "未发现 Logitech HID++ 设备"),
                new("local-ai", "本地模型引擎", engineReady ? "supported" : "on-demand",
                    engineReady ? "已配置 llama.cpp 引擎" : "可按需配置模型与推理引擎")
            };

            var plugins = Definitions.Select(definition =>
            {
                var supported = definition.Id switch
                {
                    "hardware" => true,
                    "system-tools" => true,
                    "automation" => true,
                    "ai-workbench" => true,
                    "game-enhancements" => gpu.Available &&
                                            gpu.Name.Contains(
                                                "RTX", StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
                var detail = definition.Id switch
                {
                    "hardware" => board.ControlAvailable
                        ? $"{board.FamilyName} 厂商控制可用；显卡 {gpu.Name}"
                        : $"{board.FamilyName} 已识别，硬件插件可用但写入能力取决于本地接口。",
                    "system-tools" => "Windows 卸载登记、目录扫描和事件日志可用。",
                    "automation" => "全局热键可用；宏钩子只在实际启用后安装。",
                    "ai-workbench" => engineReady
                        ? "已发现本地推理引擎；云端入口始终可选。"
                        : "插件可用，模型与 llama.cpp 引擎按需下载或导入。",
                    "game-enhancements" => supported
                        ? "NVIDIA RTX 可用；GPU Unlock 写入仍受上游 profile 门控。"
                        : "需要 NVIDIA RTX 显卡。",
                    _ => ""
                };
                return new PluginPack(
                    definition.Id,
                    definition.Name,
                    definition.Description,
                    definition.Pages,
                    definition.Delivery,
                    definition.Includes,
                    supported,
                    enabled.Contains(definition.Id),
                    detail);
            }).ToList();

            _cached = new PluginCatalogSnapshot(
                DateTimeOffset.Now,
                _settings.Current.PluginOnboardingCompleted,
                capabilities,
                plugins);
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    public PluginCatalogSnapshot SetEnabled(string id, bool enabled)
    {
        var plugin = Definitions.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("未知插件包。");
        var catalog = GetCatalog();
        var status = catalog.Plugins.First(item => item.Id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase));
        if (enabled && !status.Supported)
        {
            throw new InvalidOperationException("当前硬件不满足这个插件包的支持条件。");
        }

        var values = _settings.Current.EnabledPlugins
            .Where(item => !item.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (enabled)
        {
            values.Add(plugin.Id);
        }
        _settings.Current.EnabledPlugins = values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.Update(_settings.Current);
        _cached = null;
        return GetCatalog(refresh: true);
    }

    public PluginCatalogSnapshot CompleteOnboarding()
    {
        _settings.Current.PluginOnboardingCompleted = true;
        _settings.Update(_settings.Current);
        _cached = null;
        return GetCatalog();
    }

    public PluginCatalogSnapshot Refresh()
    {
        _cached = null;
        return GetCatalog(refresh: true);
    }

    private static int ReadDisplayCount()
    {
        try
        {
            return new BrightnessService().GetDisplays().Count(display => display.IsActive);
        }
        catch
        {
            return 0;
        }
    }

    private static int ReadLogitechCount()
    {
        try
        {
            return new LogitechHidService().GetDevices().Count(device => device.Available);
        }
        catch
        {
            return 0;
        }
    }

    private bool HasLocalModelEngine()
    {
        var directory = _settings.Current.LocalModelEngineDirectory;
        return !string.IsNullOrWhiteSpace(directory) &&
               File.Exists(Path.Combine(directory, "llama-server.exe"));
    }

    private sealed record PluginPackDefinition(
        string Id,
        string Name,
        string Description,
        string[] Pages,
        string Delivery,
        string[] Includes);
}

public sealed record PluginCatalogSnapshot(
    DateTimeOffset DetectedAt,
    bool OnboardingCompleted,
    IReadOnlyList<FeatureCapability> Capabilities,
    IReadOnlyList<PluginPack> Plugins);

public sealed record FeatureCapability(
    string Id,
    string Name,
    string Status,
    string Detail);

public sealed record PluginPack(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Pages,
    string Delivery,
    IReadOnlyList<string> Includes,
    bool Supported,
    bool Enabled,
    string Detail);
