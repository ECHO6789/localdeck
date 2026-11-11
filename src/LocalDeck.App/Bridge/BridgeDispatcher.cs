using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using LocalDeck.App.Models;
using LocalDeck.App.Services;
using Microsoft.Win32;

namespace LocalDeck.App.Bridge;

public sealed class BridgeDispatcher : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly Window _owner;
    private readonly SettingsService _settings;
    private readonly SystemMonitorService _system;
    private readonly BrightnessService _brightness;
    private readonly VendorControlService _vendor;
    private readonly HardwareProfileService _hardware;
    private readonly GpuControlService _gpuControls = new();
    private readonly GpuUnlockService _gpuUnlock = new();
    private readonly PluginCatalogService _plugins;
    private readonly ProxyService _proxy;
    private readonly ProxyProfileService _proxyProfiles;
    private readonly LogitechHidService _logitech;
    private readonly PointerService _pointer;
    private readonly ClipboardService _clipboard;
    private readonly FileIndexService _files;
    private readonly AudioService _audio;
    private readonly ScreenshotService _screenshot;
    private readonly HotkeyService _hotkeys;
    private readonly MacroService _macros;
    private readonly Lazy<DeviceManagerService> _deviceManager = new(() => new DeviceManagerService());
    private readonly Lazy<TaskStartupService> _taskStartup = new(() => new TaskStartupService());
    private readonly Lazy<UninstallerService> _uninstaller = new(() => new UninstallerService());
    private readonly Lazy<DiskAnalyzerService> _diskAnalyzer = new(() => new DiskAnalyzerService());
    private readonly Lazy<DriverService> _drivers = new(() => new DriverService());
    private readonly Lazy<WindowsLogService> _windowsLogs = new(() => new WindowsLogService());
    private readonly Lazy<ExecutableCatalogService> _executables = new(() => new ExecutableCatalogService());
    private readonly Lazy<MacroRecorderService> _macroRecorder = new(() => new MacroRecorderService());
    private readonly Lazy<BluetoothBatteryService> _bluetoothBattery = new(() => new BluetoothBatteryService());
    private readonly Lazy<LocalModelService> _localModels;
    private readonly Lazy<DlssUnlockedService> _dlssUnlocked;
    public Func<bool>? OpenMacroEditorWindow { get; set; }
    public Func<bool>? TogglePerformanceOverlay { get; set; }
    public Func<PerformanceOverlaySettingsUpdate, AppSettings>? UpdatePerformanceOverlay { get; set; }

    public BridgeDispatcher(
        Window owner,
        SettingsService settings,
        SystemMonitorService system,
        BrightnessService brightness,
        VendorControlService vendor,
        ProxyService proxy,
        ProxyProfileService proxyProfiles,
        LogitechHidService logitech,
        PointerService pointer,
        ClipboardService clipboard,
        FileIndexService files,
        AudioService audio,
        ScreenshotService screenshot,
        HotkeyService hotkeys,
        MacroService macros)
    {
        _owner = owner;
        _settings = settings;
        _system = system;
        _brightness = brightness;
        _vendor = vendor;
        _hardware = new HardwareProfileService(vendor);
        _plugins = new PluginCatalogService(settings, _hardware, _gpuControls);
        _proxy = proxy;
        _proxyProfiles = proxyProfiles;
        _logitech = logitech;
        _pointer = pointer;
        _clipboard = clipboard;
        _files = files;
        _audio = audio;
        _screenshot = screenshot;
        _hotkeys = hotkeys;
        _macros = macros;
        _localModels = new Lazy<LocalModelService>(() => new LocalModelService(settings));
        _dlssUnlocked = new Lazy<DlssUnlockedService>(() =>
            new DlssUnlockedService(() => _proxy.IsRunning ? _settings.Current.ProxyPort : null));
    }

    public async Task<string> DispatchAsync(string rawRequest)
    {
        BridgeRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(rawRequest, JsonOptions);
            if (request is null || string.IsNullOrWhiteSpace(request.Method))
            {
                throw new ArgumentException("Invalid bridge request.");
            }

            object? result = request.Method switch
            {
                "bootstrap" => Bootstrap(),
                "getOverview" => GetOverview(),
                "getDisplays" => _brightness.GetDisplays(),
                "setBrightness" => new
                {
                    ok = _brightness.SetBrightness(
                        GetString(request.Params, "id"), GetInt(request.Params, "value"))
                },
                "getVendorTelemetry" => _vendor.ReadTelemetry(),
                "getHardwareProfile" => _hardware.Read(),
                "getGpuControl" => _gpuControls.Read(),
                "setGpuControl" => await SetGpuControlAsync(request.Params),
                "getGpuUnlockStatus" => _gpuUnlock.GetStatus(),
                "exportGpuUnlockReport" => new
                {
                    path = _gpuUnlock.ExportCompatibilityReport()
                },
                "openGpuUnlockUpstream" => OpenGpuUnlockUpstream(),
                "getDlssUnlockedStatus" => _dlssUnlocked.Value.GetStatus(),
                "chooseDlssGameFolder" => ChooseFolder(
                    "选择包含游戏主 exe 的目录"),
                "startDlssDownload" => _dlssUnlocked.Value.StartDownload(),
                "cancelDlssDownload" => CancelDlssDownload(),
                "startDlssInstall" => _dlssUnlocked.Value.StartInstall(
                    GetString(request.Params, "path"),
                    GetString(request.Params, "proxyName")),
                "startDlssUninstall" => _dlssUnlocked.Value.StartUninstall(
                    GetString(request.Params, "id")),
                "openDlssInstallation" => OpenDlssInstallation(
                    GetString(request.Params, "id")),
                "openDlssUpstream" => OpenDlssUpstream(),
                "getPluginCatalog" => HasProperty(request.Params, "refresh") &&
                                      GetBool(request.Params, "refresh")
                    ? _plugins.Refresh()
                    : _plugins.GetCatalog(),
                "setPluginEnabled" => _plugins.SetEnabled(
                    GetString(request.Params, "id"),
                    GetBool(request.Params, "enabled")),
                "completePluginOnboarding" => _plugins.CompleteOnboarding(),
                "setPerformanceMode" => new
                {
                    ok = _vendor.SetPerformanceMode(GetString(request.Params, "mode"))
                },
                "getPointerSpeed" => new { value = _pointer.GetSpeed() },
                "setPointerSpeed" => new
                {
                    ok = _pointer.SetSpeed(GetInt(request.Params, "value"))
                },
                "getLogitechDevices" => await Task.Run(_logitech.GetDevices),
                "setLogitechDpi" => new
                {
                    ok = await Task.Run(() => _logitech.SetDpi(
                        GetString(request.Params, "id"), GetInt(request.Params, "dpi")))
                },
                "getProxyStatus" => _proxy.GetStatus(),
                "startProxy" => await StartProxyAsync(),
                "stopProxy" => await _proxy.StopAsync(),
                "setProxyMode" => SetProxyMode(GetString(request.Params, "mode")),
                "selectProxyNode" => SelectProxyNode(GetString(request.Params, "name")),
                "setProxySystemProxy" => await SetProxySystemProxyAsync(GetBool(request.Params, "enabled")),
                "importProxyProfile" => ImportProxyProfile(),
                "getClipboardHistory" => _clipboard.GetHistory(GetOptionalString(request.Params, "filter")),
                "copyClipboardEntry" => new
                {
                    ok = _clipboard.Copy(GetLong(request.Params, "id"))
                },
                "toggleClipboardPin" => _clipboard.TogglePinned(GetLong(request.Params, "id")),
                "deleteClipboardEntry" => _clipboard.Delete(GetLong(request.Params, "id")),
                "clearClipboardHistory" => _clipboard.Clear(GetBool(request.Params, "includePinned")),
                "addClipboardSnippet" => _clipboard.AddSnippet(GetString(request.Params, "text")),
                "captureRegion" => await _screenshot.CaptureRegionAsync(_owner, pinAfterCapture: false),
                "captureRegionAndPin" => await _screenshot.CaptureRegionAsync(_owner, pinAfterCapture: true),
                "pinClipboardImage" => new { ok = _screenshot.PinClipboardImage() },
                "captureFullScreen" => new { ok = _screenshot.CaptureFullScreen() },
                "searchFiles" => await Task.Run(() => _files.Search(
                    GetString(request.Params, "query"), GetInt(request.Params, "limit", 80))),
                "getFileIndexStatus" => _files.GetStatus(),
                "startFileIndex" => StartFileIndex(),
                "stopFileIndex" => StopFileIndex(),
                "openPath" => new { ok = _files.Open(GetString(request.Params, "path")) },
                "revealPath" => new { ok = _files.Reveal(GetString(request.Params, "path")) },
                "chooseSearchRoot" => ChooseSearchRoot(),
                "getAudioSessions" => _audio.GetSessions(),
                "setAudioVolume" => new
                {
                    ok = _audio.SetVolume(
                        GetInt(request.Params, "processId"),
                        GetInt(request.Params, "value"),
                        HasProperty(request.Params, "muted") ? GetBool(request.Params, "muted") : null)
                },
                "getDevices" => await Task.Run(_deviceManager.Value.GetDevices),
                "getBluetoothBattery" => await _bluetoothBattery.Value.GetConnectedBatteryLevelsAsync(),
                "setDeviceEnabled" => new
                {
                    ok = _deviceManager.Value.SetEnabled(
                        GetString(request.Params, "instanceId"),
                        GetBool(request.Params, "enabled"))
                },
                "rescanDevices" => new { ok = _deviceManager.Value.Rescan() },
                "openWindowsDeviceManager" => OpenWindowsDeviceManager(),
                "openDeviceProperties" => OpenDeviceProperties(GetString(request.Params, "instanceId")),
                "getStartupItems" => await Task.Run(_taskStartup.Value.GetStartupItems),
                "cleanupOrphanedStartup" => _taskStartup.Value.CleanupOrphanedStartupItems(),
                "setStartupEnabled" => SetStartupEnabled(request.Params),
                "getScheduledTasks" => await Task.Run(_taskStartup.Value.GetScheduledTasks),
                "setScheduledTaskEnabled" => SetScheduledTaskEnabled(request.Params),
                "runScheduledTask" => RunScheduledTask(request.Params),
                "stopScheduledTask" => StopScheduledTask(request.Params),
                "getHotkeyStatus" => _hotkeys.Registrations,
                "runMacro" => new { ok = _macros.RunMacro(GetString(request.Params, "id")) },
                "typeText" => TypeText(GetString(request.Params, "text")),
                "getInstalledApps" => await Task.Run(_uninstaller.Value.GetInstalledApps),
                "launchUninstaller" => LaunchUninstaller(request.Params),
                "scanLeftovers" => ScanLeftovers(request.Params),
                "quarantineDirectory" => QuarantineDirectory(request.Params),
                "deleteRegistryLeftover" => DeleteRegistryLeftover(request.Params),
                "startDiskAnalysis" => _diskAnalyzer.Value.Start(GetString(request.Params, "root")),
                "getDiskRoots" => DriveInfo.GetDrives()
                    .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
                    .Select(drive => drive.Name)
                    .ToList(),
                "getDiskAnalysis" => _diskAnalyzer.Value.GetStatus(),
                "cancelDiskAnalysis" => CancelDiskAnalysis(),
                "getDrivers" => await Task.Run(_drivers.Value.GetInstalledDrivers),
                "checkDriverUpdates" => await CheckDriverUpdatesAsync(),
                "getWindowsLogs" => await GetWindowsLogsAsync(request.Params),
                "getExecutables" => await Task.Run(() => _executables.Value.GetExecutables(
                    GetOptionalString(request.Params, "query"), GetInt(request.Params, "limit", 1200))),
                "launchExecutable" => LaunchExecutable(request.Params),
                "toggleExecutablePin" => ToggleExecutablePin(request.Params),
                "getLocalModels" => await Task.Run(_localModels.Value.GetModels),
                "getLocalModelEngine" => _localModels.Value.GetEngineInfo(),
                "adoptLocalModelEngine" => await _localModels.Value.AdoptEngineAsync(),
                "chooseLocalModelDirectory" => ChooseFolder(
                    "选择存放 GGUF 模型的目录"),
                "chooseLocalModelEngine" => ChooseFolder(
                    "选择包含 llama-server.exe 的 llama.cpp 引擎目录"),
                "startModelDownload" => _localModels.Value.StartDownload(
                    GetString(request.Params, "url"), GetString(request.Params, "fileName")),
                "getModelDownloadStatus" => _localModels.Value.GetDownloadStatus(),
                "cancelModelDownload" => CancelModelDownload(),
                "saveModelArguments" => SaveModelArguments(request.Params),
                "benchmarkModel" => await BenchmarkModelAsync(request.Params),
                "startModelServer" => StartModelServer(request.Params),
                "stopModelServer" => new { ok = _localModels.Value.StopServer() },
                "openCloudModel" => OpenCloudModel(GetString(request.Params, "id")),
                "openUrl" => OpenUrl(GetString(request.Params, "url")),
                "startMacroRecording" => new { ok = _macroRecorder.Value.Start() },
                "stopMacroRecording" => new { steps = _macroRecorder.Value.Stop() },
                "getMacroRecording" => new
                {
                    recording = _macroRecorder.Value.IsRecording,
                    steps = _macroRecorder.Value.GetSteps()
                },
                "openMacroWindow" => new { ok = OpenMacroEditorWindow?.Invoke() ?? false },
                "togglePerformanceOverlay" => new
                {
                    enabled = TogglePerformanceOverlay?.Invoke() ?? false
                },
                "updatePerformanceOverlay" => UpdatePerformanceOverlaySettings(request.Params),
                "getOverlayOptions" => GetOverlayOptions(),
                "getSettings" => _settings.Current,
                "saveSettings" => SaveSettings(request.Params),
                "openDataFolder" => OpenDataFolder(),
                "minimizeWindow" => MinimizeWindow(),
                _ => throw new InvalidOperationException($"Unknown bridge method: {request.Method}")
            };

            return JsonSerializer.Serialize(new
            {
                id = request.Id,
                ok = true,
                result
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Bridge {request?.Method ?? "parse"} failed: {ex}");
            return JsonSerializer.Serialize(new
            {
                id = request?.Id,
                ok = false,
                error = ex.Message
            }, JsonOptions);
        }
    }

    private object Bootstrap()
    {
        return new
        {
            app = new
            {
                name = "LocalDeck",
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0",
                machine = Environment.MachineName,
                os = Environment.OSVersion.VersionString
            },
            settings = _settings.Current,
            capabilities = new
            {
                proxy = !string.IsNullOrWhiteSpace(_settings.Current.ProxyProfilePath),
                searchIndex = _files.GetStatus(),
                minimizedLaunch = Environment.GetCommandLineArgs().Contains("--minimized")
            }
        };
    }

    private object LaunchUninstaller(JsonElement parameters)
    {
        var app = JsonSerializer.Deserialize<InstalledApp>(
            parameters.GetProperty("app").GetRawText(), JsonOptions)
            ?? throw new ArgumentException("Invalid app.");
        return new { ok = _uninstaller.Value.LaunchUninstaller(app) };
    }

    private object ScanLeftovers(JsonElement parameters)
    {
        var app = JsonSerializer.Deserialize<InstalledApp>(
            parameters.GetProperty("app").GetRawText(), JsonOptions)
            ?? throw new ArgumentException("Invalid app.");
        return _uninstaller.Value.ScanLeftovers(app);
    }

    private object QuarantineDirectory(JsonElement parameters)
    {
        var item = JsonSerializer.Deserialize<AppLeftover>(
            parameters.GetProperty("item").GetRawText(), JsonOptions)
            ?? throw new ArgumentException("Invalid leftover.");
        return new { path = _uninstaller.Value.QuarantineDirectory(item) };
    }

    private object DeleteRegistryLeftover(JsonElement parameters)
    {
        var item = JsonSerializer.Deserialize<AppLeftover>(
            parameters.GetProperty("item").GetRawText(), JsonOptions)
            ?? throw new ArgumentException("Invalid leftover.");
        return new { backup = _uninstaller.Value.DeleteRegistryWithBackup(item) };
    }

    private object CancelDiskAnalysis()
    {
        _diskAnalyzer.Value.Cancel();
        return _diskAnalyzer.Value.GetStatus();
    }

    private async Task<IReadOnlyList<DriverUpdateInfo>> CheckDriverUpdatesAsync()
    {
        return await _drivers.Value.CheckWindowsUpdateDriversAsync();
    }

    private async Task<WindowsLogReport> GetWindowsLogsAsync(JsonElement parameters)
    {
        var hours = GetInt(parameters, "hours", 24);
        return await _windowsLogs.Value.AnalyzeAsync(
            TimeSpan.FromHours(Math.Clamp(hours, 1, 24 * 30)));
    }

    private object LaunchExecutable(JsonElement parameters)
    {
        var item = JsonSerializer.Deserialize<ExecutableItem>(
            parameters.GetProperty("item").GetRawText(), JsonOptions)
            ?? throw new ArgumentException("Invalid executable.");
        return new
        {
            ok = _executables.Value.Launch(item, GetOptionalString(parameters, "arguments") ?? "")
        };
    }

    private object ToggleExecutablePin(JsonElement parameters)
    {
        var item = JsonSerializer.Deserialize<ExecutableItem>(
            parameters.GetProperty("item").GetRawText(), JsonOptions)
            ?? throw new ArgumentException("Invalid executable.");
        return new { ok = _executables.Value.TogglePin(item) };
    }

    private object SaveModelArguments(JsonElement parameters)
    {
        _localModels.Value.SetLaunchArguments(
            GetString(parameters, "path"), GetString(parameters, "arguments"));
        return _settings.Current.LocalModelArguments;
    }

    private async Task<object> BenchmarkModelAsync(JsonElement parameters)
    {
        var path = GetString(parameters, "path");
        var model = _localModels.Value.GetModels().FirstOrDefault(item => item.Path == path)
            ?? throw new ArgumentException("Model not found.");
        return new
        {
            output = await _localModels.Value.BenchmarkAsync(model, CancellationToken.None)
        };
    }

    private object StartModelServer(JsonElement parameters)
    {
        var path = GetString(parameters, "path");
        var model = _localModels.Value.GetModels().FirstOrDefault(item => item.Path == path)
            ?? throw new ArgumentException("Model not found.");
        return _localModels.Value.StartServer(model);
    }

    private object CancelModelDownload()
    {
        _localModels.Value.CancelDownload();
        return _localModels.Value.GetDownloadStatus();
    }

    private object OpenCloudModel(string id)
    {
        _localModels.Value.OpenCloudModel(id);
        return new { ok = true };
    }

    private static object OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Only HTTP(S) URLs can be opened.");
        }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return new { ok = true };
    }

    private object GetOverlayOptions()
    {
        var processes = Process.GetProcesses()
            .Select(process =>
            {
                try
                {
                    return process.ProcessName;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    process.Dispose();
                }
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .Take(300)
            .ToList();
        var displayOptions = _brightness.GetDisplays()
            .Where(display => !string.IsNullOrWhiteSpace(display.DeviceName))
            .Select(display => new
            {
                id = display.Id,
                name = display.Name,
                deviceName = display.DeviceName!
            })
            .ToList();
        var knownScreens = displayOptions
            .Select(display => display.deviceName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            if (!knownScreens.Add(screen.DeviceName))
            {
                continue;
            }
            displayOptions.Add(new
            {
                id = $"screen:{screen.DeviceName}",
                name = screen.Primary ? "主屏幕" : $"屏幕 {displayOptions.Count + 1}",
                deviceName = screen.DeviceName
            });
        }
        return new { processes, displays = displayOptions };
    }

    private async Task<GpuControlResult> SetGpuControlAsync(JsonElement parameters)
    {
        return await _gpuControls.ApplyElevatedAsync(
            GetString(parameters, "kind"),
            GetInt(parameters, "value"));
    }

    private object OpenGpuUnlockUpstream()
    {
        _gpuUnlock.OpenUpstream();
        return new { ok = true };
    }

    private DlssUnlockedStatus CancelDlssDownload()
    {
        _dlssUnlocked.Value.Cancel();
        return _dlssUnlocked.Value.GetStatus();
    }

    private object OpenDlssInstallation(string id)
    {
        _dlssUnlocked.Value.OpenInstallation(id);
        return new { ok = true };
    }

    private object OpenDlssUpstream()
    {
        _dlssUnlocked.Value.OpenUpstream();
        return new { ok = true };
    }

    private AppSettings UpdatePerformanceOverlaySettings(JsonElement parameters)
    {
        var update = JsonSerializer.Deserialize<PerformanceOverlaySettingsUpdate>(
            parameters.GetRawText(), JsonOptions)
            ?? throw new ArgumentException("Invalid overlay settings.");
        return UpdatePerformanceOverlay?.Invoke(update)
            ?? throw new InvalidOperationException("Performance overlay is unavailable.");
    }

    private object GetOverview()
    {
        return new
        {
            system = _system.Read(),
            vendor = _vendor.ReadTelemetry(),
            pointerSpeed = _pointer.GetSpeed(),
            index = _files.GetStatus(),
            proxy = _proxy.GetStatus(),
            timestamp = DateTimeOffset.Now
        };
    }

    private async Task<ProxyStatus> StartProxyAsync()
    {
        var status = await _proxy.StartAsync();
        if (_settings.Current.ProxySystemProxyEnabled)
        {
            _proxy.SetSystemProxy(true);
        }
        return status;
    }

    private ProxyStatus SetProxyMode(string mode)
    {
        if (mode is not ("rule" or "global" or "direct"))
        {
            throw new ArgumentException("Unsupported proxy mode.");
        }
        _settings.Current.ProxyMode = mode;
        _settings.Update(_settings.Current);
        return _proxy.GetStatus();
    }

    private ProxyStatus SelectProxyNode(string name)
    {
        _settings.Current.ProxySelectedNode = name;
        _settings.Update(_settings.Current);
        return _proxy.GetStatus();
    }

    private async Task<ProxyStatus> SetProxySystemProxyAsync(bool enabled)
    {
        if (enabled && !_proxy.IsRunning)
        {
            await _proxy.StartAsync();
        }
        _settings.Current.ProxySystemProxyEnabled = enabled;
        _proxy.SetSystemProxy(enabled);
        _settings.Update(_settings.Current);
        return _proxy.GetStatus();
    }

    private ProxyStatus ImportProxyProfile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入代理 YAML",
            Filter = "代理配置 (*.yaml;*.yml)|*.yaml;*.yml|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return _proxy.GetStatus();
        }

        _settings.Current.ProxyProfilePath = _proxyProfiles.Import(dialog.FileName);
        var supported = _proxyProfiles.GetNodeInfos().FirstOrDefault(item => item.Supported);
        _settings.Current.ProxySelectedNode = supported?.Name;
        _settings.Update(_settings.Current);
        _proxy.ApplySettings();
        return _proxy.GetStatus();
    }

    private object SetStartupEnabled(JsonElement parameters)
    {
        var item = JsonSerializer.Deserialize<StartupItem>(
            parameters.GetProperty("item").GetRawText(), JsonOptions)
            ?? throw new ArgumentException("Invalid startup item.");
        var enabled = GetBool(parameters, "enabled");
        if (item.Scope == "HKCU" &&
            item.Kind == "Run" &&
            item.Name.Equals("LocalDeck", StringComparison.OrdinalIgnoreCase))
        {
            _settings.Current.LaunchAtLogin = enabled;
            _settings.Update(_settings.Current);
            SetLaunchAtLogin(enabled);
            return new { ok = true };
        }

        return new
        {
            ok = _taskStartup.Value.SetStartupEnabled(item, enabled)
        };
    }

    private object SetScheduledTaskEnabled(JsonElement parameters)
    {
        var item = JsonSerializer.Deserialize<ScheduledTaskInfo>(
            parameters.GetProperty("item").GetRawText(), JsonOptions)
            ?? throw new ArgumentException("Invalid scheduled task.");
        return new
        {
            ok = _taskStartup.Value.SetScheduledTaskEnabled(item, GetBool(parameters, "enabled"))
        };
    }

    private object RunScheduledTask(JsonElement parameters)
    {
        var item = JsonSerializer.Deserialize<ScheduledTaskInfo>(
            parameters.GetProperty("item").GetRawText(), JsonOptions)
            ?? throw new ArgumentException("Invalid scheduled task.");
        return new { ok = _taskStartup.Value.RunScheduledTask(item) };
    }

    private object StopScheduledTask(JsonElement parameters)
    {
        var item = JsonSerializer.Deserialize<ScheduledTaskInfo>(
            parameters.GetProperty("item").GetRawText(), JsonOptions)
            ?? throw new ArgumentException("Invalid scheduled task.");
        return new { ok = _taskStartup.Value.StopScheduledTask(item) };
    }

    private object OpenWindowsDeviceManager()
    {
        _deviceManager.Value.OpenDeviceManager();
        return new { ok = true };
    }

    private object OpenDeviceProperties(string instanceId)
    {
        _deviceManager.Value.OpenProperties(instanceId);
        return new { ok = true };
    }

    private object TypeText(string text)
    {
        _macros.TypeText(text);
        return new { ok = true };
    }

    private object StartFileIndex()
    {
        _files.StartIndex(_settings.Current.SearchRoots);
        return _files.GetStatus();
    }

    private object StopFileIndex()
    {
        _files.StopIndex();
        return _files.GetStatus();
    }

    private object ChooseSearchRoot()
    {
        return ChooseFolder("选择需要纳入本地文件索引的目录");
    }

    private static object ChooseFolder(string description)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        return new
        {
            path = dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                ? dialog.SelectedPath
                : null
        };
    }

    private object SaveSettings(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("settings", out var element))
        {
            throw new ArgumentException("settings is required.");
        }

        var incoming = element.Deserialize<AppSettings>(JsonOptions)
            ?? throw new ArgumentException("Invalid settings.");
        _settings.Update(incoming);
        SetLaunchAtLogin(_settings.Current.LaunchAtLogin);
        return _settings.Current;
    }

    private object OpenDataFolder()
    {
        AppPaths.EnsureCreated();
        Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });
        return new { ok = true, path = AppPaths.DataDirectory };
    }

    private object MinimizeWindow()
    {
        _owner.Hide();
        return new { ok = true };
    }

    private static void SetLaunchAtLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? throw new InvalidOperationException("Startup registry key is unavailable.");
        if (enabled)
        {
            key.SetValue("LocalDeck", $"\"{Environment.ProcessPath}\" --minimized");
        }
        else
        {
            key.DeleteValue("LocalDeck", throwOnMissingValue: false);
        }
    }

    private static string GetString(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value)
            ? value.GetString() ?? throw new ArgumentException($"{name} must be a string.")
            : throw new ArgumentException($"{name} is required.");

    private static string? GetOptionalString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement parameters, string name, int fallback = 0) =>
        parameters.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static long GetLong(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number
            : throw new ArgumentException($"{name} must be an integer.");

    private static bool GetBool(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static bool HasProperty(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out _);

    public void Dispose()
    {
        if (_diskAnalyzer.IsValueCreated)
        {
            _diskAnalyzer.Value.Dispose();
        }
        if (_localModels.IsValueCreated)
        {
            _localModels.Value.Dispose();
        }
        if (_dlssUnlocked.IsValueCreated)
        {
            _dlssUnlocked.Value.Dispose();
        }
        if (_macroRecorder.IsValueCreated)
        {
            _macroRecorder.Value.Dispose();
        }
        _gpuControls.Dispose();
    }

    private sealed record BridgeRequest(string Id, string Method, JsonElement Params);
}
