using System.Text.Json;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using HidSharp;
using LocalDeck.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Contains("--hid-raw", StringComparer.OrdinalIgnoreCase))
{
    DumpHidFeatures();
    return;
}
if (args.Contains("--verify-writes", StringComparer.OrdinalIgnoreCase))
{
    await VerifySafeWrites();
    return;
}
if (args.Contains("--proxy-smoke", StringComparer.OrdinalIgnoreCase))
{
    await VerifyProxyCore();
    return;
}
if (args.Contains("--hysteria-smoke", StringComparer.OrdinalIgnoreCase))
{
    await VerifyHysteria2();
    return;
}
if (args.Contains("--rule-proxy-smoke", StringComparer.OrdinalIgnoreCase))
{
    await VerifyRuleProxy();
    return;
}
if (args.Contains("--proxy-hold", StringComparer.OrdinalIgnoreCase))
{
    await HoldProxy();
    return;
}
if (args.Contains("--management-smoke", StringComparer.OrdinalIgnoreCase))
{
    DumpManagement();
    return;
}
if (args.Contains("--installed-apps", StringComparer.OrdinalIgnoreCase))
{
    DumpInstalledApps();
    return;
}
if (args.Contains("--adopt-model-engine", StringComparer.OrdinalIgnoreCase))
{
    await AdoptModelEngine();
    return;
}
if (args.Contains("--model-smoke", StringComparer.OrdinalIgnoreCase))
{
    await ModelSmoke();
    return;
}
if (args.Contains("--replacement-smoke", StringComparer.OrdinalIgnoreCase))
{
    await ReplacementSmoke();
    return;
}
if (args.Contains("--display-smoke", StringComparer.OrdinalIgnoreCase))
{
    DisplaySmoke();
    return;
}
if (args.Contains("--display-toggle-smoke", StringComparer.OrdinalIgnoreCase))
{
    DisplayToggleSmoke();
    return;
}
if (args.Contains("--fps-smoke", StringComparer.OrdinalIgnoreCase))
{
    FpsSmoke();
    return;
}
if (args.Contains("--screen-fps-smoke", StringComparer.OrdinalIgnoreCase))
{
    using var fps = new FpsMonitorService();
    var sample = fps.Sample(@"screen:\\.\DISPLAY5");
    Console.WriteLine(
        $"Screen={sample.ProcessName}, FPS={sample.Fps}, Refresh={sample.RefreshHz}, " +
        $"Available={sample.EtwAvailable}");
    return;
}
if (args.Contains("--overlay-smoke", StringComparer.OrdinalIgnoreCase))
{
    await OverlaySmoke();
    return;
}
if (args.Contains("--hardware-control-smoke", StringComparer.OrdinalIgnoreCase))
{
    var profile = new HardwareProfileService(new VendorControlService()).Read();
    using var gpuControl = new GpuControlService();
    var gpu = gpuControl.Read();
    Console.WriteLine(
        $"Board={profile.Manufacturer}/{profile.Product}, Family={profile.FamilyName}, " +
        $"Panel={profile.PanelName}, Control={profile.ControlAvailable}, " +
        $"Interfaces={string.Join(",", profile.DetectedInterfaces)}");
    Console.WriteLine(
        $"GPU={gpu.Name}, Power={gpu.PowerUsageWatts:0.0}W/{gpu.PowerLimitWatts:0}W, " +
        $"Clock={gpu.GraphicsClockMhz}/{gpu.MemoryClockMhz}MHz, " +
        $"Fan={gpu.FanPercent}, PowerWritable={gpu.PowerWritable}, " +
        $"FanWritable={gpu.FanWritable}, CoreOffset={gpu.CoreOffsetMhz}, " +
        $"MemOffset={gpu.MemoryOffsetMhz}, Error={gpu.Error ?? "none"}");
    return;
}
if (args.Contains("--unlock-smoke", StringComparer.OrdinalIgnoreCase))
{
    var unlock = new GpuUnlockService().GetStatus();
    using var dlss = new DlssUnlockedService(() => null);
    var dlssStatus = dlss.GetStatus();
    Console.WriteLine(
        $"GPU={unlock.Name}, PCI={unlock.VendorId}:{unlock.DeviceId}, " +
        $"SUB={unlock.SubsystemVendorId}:{unlock.SubsystemDeviceId}, " +
        $"Inventory={unlock.InventorySupported}, Profile={unlock.MatchedProfileId ?? "none"}, " +
        $"WriteEnabled={unlock.ProfileWriteEnabled}");
    Console.WriteLine(
        $"DLSS={dlssStatus.Version}, PackageReady={dlssStatus.PackageReady}, " +
        $"Bytes={dlssStatus.Download.TotalBytes}, GPU={dlssStatus.GpuSupported}, " +
        $"Installs={dlssStatus.Installations.Count}, Hash={dlssStatus.PackageSha256}");
    return;
}
if (args.Contains("--plugin-smoke", StringComparer.OrdinalIgnoreCase))
{
    var pluginSettings = new SettingsService();
    pluginSettings.Load();
    var vendor = new VendorControlService();
    var hardware = new HardwareProfileService(vendor);
    using var gpu = new GpuControlService();
    var catalog = new PluginCatalogService(pluginSettings, hardware, gpu).Refresh();
    Console.WriteLine(
        $"Plugins={catalog.Plugins.Count}, Capabilities={catalog.Capabilities.Count}, " +
        $"Onboarding={catalog.OnboardingCompleted}, Enabled={string.Join(",", pluginSettings.Current.EnabledPlugins)}");
    foreach (var plugin in catalog.Plugins)
    {
        Console.WriteLine(
            $"{plugin.Id}\tsupported={plugin.Supported}\tenabled={plugin.Enabled}\t{plugin.Detail}");
    }
    foreach (var capability in catalog.Capabilities)
    {
        Console.WriteLine(
            $"{capability.Id}\t{capability.Status}\t{capability.Detail}");
    }
    return;
}
if (args.Contains("--plugin-toggle-smoke", StringComparer.OrdinalIgnoreCase))
{
    var toggleSettings = new SettingsService();
    toggleSettings.Load();
    var original = toggleSettings.Current.EnabledPlugins.ToList();
    var originalOnboarding = toggleSettings.Current.PluginOnboardingCompleted;
    var vendor = new VendorControlService();
    var hardware = new HardwareProfileService(vendor);
    using var gpu = new GpuControlService();
    var catalog = new PluginCatalogService(toggleSettings, hardware, gpu);
    try
    {
        catalog.SetEnabled("hardware", true);
        var enabled = toggleSettings.Current.EnabledPlugins.Contains("hardware");
        catalog.SetEnabled("hardware", false);
        var disabled = !toggleSettings.Current.EnabledPlugins.Contains("hardware");
        Console.WriteLine($"Plugin toggle pass: enabled={enabled}, disabled={disabled}");
    }
    finally
    {
        toggleSettings.Current.EnabledPlugins = original;
        toggleSettings.Current.PluginOnboardingCompleted = originalOnboarding;
        toggleSettings.Update(toggleSettings.Current);
    }
    return;
}
if (args.Contains("--dlss-download-smoke", StringComparer.OrdinalIgnoreCase))
{
    await DlssDownloadSmoke();
    return;
}
if (args.Contains("--dlss-deploy-smoke", StringComparer.OrdinalIgnoreCase))
{
    await DlssDeploySmoke();
    return;
}
if (args.Contains("--overlay-live-smoke", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(await LocalDeck.Diagnostics.OverlayLiveSmoke.RunAsync());
    return;
}
if (args.Contains("--overlay-config-smoke", StringComparer.OrdinalIgnoreCase))
{
    var overlaySettings = new SettingsService();
    overlaySettings.Load();
    overlaySettings.Current.PerformanceOverlayEnabled = true;
    overlaySettings.Current.PerformanceOverlayOpacity = 0.68;
    overlaySettings.Current.PerformanceOverlayFpsSource = "process:chrome";
    overlaySettings.Current.PerformanceOverlayMetrics =
    [
        "cpuUsage", "cpuTemp", "cpuFan", "gpuUsage", "gpuTemp", "gpuFan",
        "gpuMemory", "memory", "networkDown", "networkUp", "fps", "battery",
        "bluetooth", "disk", "uptime"
    ];
    overlaySettings.Update(overlaySettings.Current);
    Console.WriteLine("Overlay configured with all stable metrics.");
    return;
}
if (args.Contains("--overlay-reset-defaults", StringComparer.OrdinalIgnoreCase))
{
    var overlaySettings = new SettingsService();
    overlaySettings.Load();
    overlaySettings.Current.PerformanceOverlayEnabled = true;
    overlaySettings.Current.PerformanceOverlayOpacity = 0.9;
    overlaySettings.Current.PerformanceOverlayFpsSource = "foreground";
    overlaySettings.Current.PerformanceOverlayMetrics =
    [
        "cpuUsage", "cpuTemp", "gpuUsage", "gpuTemp", "gpuMemory",
        "fps", "bluetooth"
    ];
    overlaySettings.Update(overlaySettings.Current);
    Console.WriteLine("Overlay defaults restored.");
    return;
}
if (args.Contains("--logitech-smoke", StringComparer.OrdinalIgnoreCase))
{
    foreach (var device in new LogitechHidService().GetDevices())
    {
        Console.WriteLine(
            $"{device.Name}\tbattery={device.BatteryPercent}\tdpi={device.Dpi}\t" +
            $"index={device.DeviceIndex}\twritable={device.Writable}\terror={device.Error}");
    }
    return;
}
if (args.Contains("--logitech-dpi-smoke", StringComparer.OrdinalIgnoreCase))
{
    var service = new LogitechHidService();
    var device = service.GetDevices().FirstOrDefault(item => item.Writable && item.Dpi is not null);
    if (device is null)
    {
        Console.WriteLine("Writable Logitech DPI device not found.");
        return;
    }

    var original = device.Dpi!.Value;
    var target = original == 2400 ? 3200 : 2400;
    Console.WriteLine($"Device={device.Name} original={original} target={target}");
    Console.WriteLine($"Set={service.SetDpi(device.Id, target)}");
    var changed = service.GetDevices().FirstOrDefault(item => item.Id == device.Id)?.Dpi;
    Console.WriteLine($"ReadBack={changed}");
    Console.WriteLine($"Restore={service.SetDpi(device.Id, original)}");
    return;
}
if (args.Contains("--task-toggle-smoke", StringComparer.OrdinalIgnoreCase))
{
    ToggleDiagnosticTask();
    return;
}
if (args.Contains("--startup-toggle-smoke", StringComparer.OrdinalIgnoreCase))
{
    ToggleDiagnosticStartup();
    return;
}
if (args.Contains("--startup-cleanup", StringComparer.OrdinalIgnoreCase))
{
    var result = new TaskStartupService().CleanupOrphanedStartupItems();
    Console.WriteLine($"Removed={string.Join(", ", result.Removed)}");
    Console.WriteLine($"Failed={string.Join(", ", result.Failed)}");
    return;
}
if (args.Contains("--restore-proxy-port", StringComparer.OrdinalIgnoreCase))
{
    var restoreSettings = new SettingsService();
    restoreSettings.Load();
    restoreSettings.Current.ProxyPort = 7897;
    restoreSettings.Current.ProxyAutoStart = true;
    restoreSettings.Current.ProxySystemProxyEnabled = true;
    restoreSettings.Update(restoreSettings.Current);
    Console.WriteLine($"ProxyPort={restoreSettings.Current.ProxyPort}");
    return;
}
if (args.Contains("--hid-native", StringComparer.OrdinalIgnoreCase))
{
    await DumpNativeHid();
    return;
}
if (args.Contains("--hidpp-probe", StringComparer.OrdinalIgnoreCase))
{
    await DumpHidppProbe();
    return;
}
if (args.Contains("--macro-smoke", StringComparer.OrdinalIgnoreCase))
{
    MacroSmoke();
    return;
}
if (args.Contains("--tools-smoke", StringComparer.OrdinalIgnoreCase))
{
    await DumpGeneralTools();
    return;
}

var json = new JsonSerializerOptions { WriteIndented = true };
var settings = new SettingsService();
settings.Load();

Console.WriteLine("LocalDeck diagnostics");
Console.WriteLine($"Data: {AppPaths.DataDirectory}");
Console.WriteLine($"Machine: {Environment.MachineName}");
Console.WriteLine();

await Check("Vendor / fans", () =>
{
    var telemetry = new VendorControlService().ReadTelemetry();
    Console.WriteLine(JsonSerializer.Serialize(telemetry, json));
});

await Check("Displays", () =>
{
    var displays = new BrightnessService().GetDisplays();
    Console.WriteLine(JsonSerializer.Serialize(displays, json));
});

await Check("Proxy", () =>
{
    var profiles = new ProxyProfileService();
    profiles.TryLoad(settings.Current.ProxyProfilePath);
    var proxy = new ProxyService(settings, profiles);
    proxy.ApplySettings();
    Console.WriteLine(JsonSerializer.Serialize(proxy.GetStatus(), json));
});

await Check("Logitech HID++", () =>
{
    var devices = new LogitechHidService().GetDevices();
    Console.WriteLine(JsonSerializer.Serialize(devices, json));
});

await Check("File index", () =>
{
    Console.WriteLine(JsonSerializer.Serialize(new FileIndexService().GetStatus(), json));
});

await Check("Audio sessions", () =>
{
    Console.WriteLine(JsonSerializer.Serialize(new AudioService().GetSessions(), json));
});

static async Task Check(string name, Action action)
{
    Console.WriteLine($"== {name} ==");
    try
    {
        action();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine();
    await Task.CompletedTask;
}

static void DumpHidFeatures()
{
    foreach (var device in DeviceList.Local.GetHidDevices(0x046D, null, null, null))
    {
        Console.WriteLine(
            $"PATH {device.DevicePath}\n  PID={device.ProductID:X4} IN={device.GetMaxInputReportLength()} OUT={device.GetMaxOutputReportLength()}");
        if (device.ProductID == 0xC232 ||
            device.GetMaxInputReportLength() < 7 ||
            device.GetMaxOutputReportLength() < 7)
        {
            continue;
        }

        try
        {
            using var stream = device.Open();
            stream.ReadTimeout = 120;
            stream.WriteTimeout = 300;
            foreach (byte deviceIndex in new byte[] { 0xFF, 0x00, 1, 2, 3 })
            {
                foreach (ushort feature in new ushort[] { 0x0000, 0x0001, 0x0003, 0x0005, 0x1000, 0x1004, 0x2201 })
                {
                    var output = new byte[Math.Max(20, device.GetMaxOutputReportLength())];
                    output[0] = 0x11;
                    output[1] = deviceIndex;
                    output[2] = 0x00;
                    output[3] = 0x0B;
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), feature);
                    var drain = new byte[Math.Max(20, device.GetMaxInputReportLength())];
                    for (var flush = 0; flush < 8; flush++)
                    {
                        try
                        {
                            _ = stream.Read(drain, 0, drain.Length);
                        }
                        catch
                        {
                            break;
                        }
                    }

                    stream.Write(output, 0, output.Length);
                    stream.Flush();
                    var input = new byte[Math.Max(20, device.GetMaxInputReportLength())];
                    try
                    {
                        var found = false;
                        for (var attempt = 0; attempt < 8; attempt++)
                        {
                            var read = stream.Read(input, 0, input.Length);
                            Console.WriteLine(
                                $"  dev={deviceIndex} get {feature:X4} rx -> {Convert.ToHexString(input.AsSpan(0, Math.Min(read, 12)))}");
                            if (input[0] == 0x11 && input[1] == deviceIndex && input[2] == 0x00 && input[3] == 0x0B)
                            {
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            Console.WriteLine($"  dev={deviceIndex} get {feature:X4} -> no matching reply");
                        }
                    }
                    catch (TimeoutException)
                    {
                        Console.WriteLine($"  dev={deviceIndex} get {feature:X4} -> timeout");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  OPEN ERROR: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

static async Task VerifySafeWrites()
{
    var settings = new SettingsService();
    settings.Load();
    var brightness = new BrightnessService();
    foreach (var display in brightness.GetDisplays())
    {
        Console.WriteLine(
            $"Brightness {display.Name} current={display.Current} write={brightness.SetBrightness(display.Id, display.Current)}");
    }

    var pointer = new PointerService();
    var speed = pointer.GetSpeed();
    Console.WriteLine($"Pointer speed current={speed} write={pointer.SetSpeed(speed)}");
    var profiles = new ProxyProfileService();
    profiles.TryLoad(settings.Current.ProxyProfilePath);
    var proxy = new ProxyService(settings, profiles);
    Console.WriteLine($"System proxy retained={proxy.GetStatus().SystemProxyEnabled}");
}

static async Task VerifyProxyCore()
{
    var settings = new SettingsService();
    settings.Load();
    var profiles = new ProxyProfileService();
    if (string.IsNullOrWhiteSpace(settings.Current.ProxyProfilePath))
    {
        Console.WriteLine("Proxy profile not available.");
        return;
    }

    if (!profiles.TryLoad(settings.Current.ProxyProfilePath))
    {
        Console.WriteLine("Proxy profile could not be loaded.");
        return;
    }
    var nodes = profiles.GetNodeInfos();
    settings.Current.ProxyPort = 17897;
    settings.Current.ProxyMode = "global";
    settings.Current.ProxySelectedNode =
        nodes.FirstOrDefault(node => node.Type == "ss" && node.Supported)?.Name ??
        nodes.FirstOrDefault(node => node.Supported)?.Name;
    var proxy = new ProxyService(settings, profiles);
    try
    {
        await proxy.StartAsync();
        Console.WriteLine($"Proxy listening on 127.0.0.1:{settings.Current.ProxyPort}");
        using var handler = new HttpClientHandler
        {
            Proxy = new System.Net.WebProxy("http://127.0.0.1:17897"),
            UseProxy = true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await client.GetAsync("https://www.gstatic.com/generate_204");
        Console.WriteLine($"Proxy HTTPS smoke status={(int)response.StatusCode}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Proxy smoke failed: {ex}");
    }
    finally
    {
        await proxy.StopAsync(disableSystemProxy: false);
    }
}

static async Task VerifyHysteria2()
{
    var settings = new SettingsService();
    settings.Load();
    var profiles = new ProxyProfileService();
    if (string.IsNullOrWhiteSpace(settings.Current.ProxyProfilePath))
    {
        Console.WriteLine("Proxy profile not available.");
        return;
    }

    if (!profiles.TryLoad(settings.Current.ProxyProfilePath))
    {
        Console.WriteLine("Proxy profile could not be loaded.");
        return;
    }
    var node = profiles.GetNodeInfos()
        .FirstOrDefault(item => item.Type is "hysteria2" or "hy2");
    if (node is null)
    {
        Console.WriteLine("Hysteria2 node not found.");
        return;
    }

    var originalPort = settings.Current.ProxyPort;
    var originalMode = settings.Current.ProxyMode;
    var originalNode = settings.Current.ProxySelectedNode;
    Console.WriteLine($"Hysteria2 engine available={Hysteria2EngineService.IsEngineAvailable}");
    settings.Current.ProxyPort = 17898;
    settings.Current.ProxyMode = "global";
    settings.Current.ProxySelectedNode = node.Name;
    var proxy = new ProxyService(settings, profiles);
    try
    {
        await proxy.StartAsync();
        using var handler = new HttpClientHandler
        {
            Proxy = new System.Net.WebProxy("http://127.0.0.1:17898"),
            UseProxy = true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        using var response = await client.GetAsync("https://www.gstatic.com/generate_204");
        Console.WriteLine($"Hysteria2 HTTPS smoke status={(int)response.StatusCode}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Hysteria2 smoke failed: {ex}");
    }
    finally
    {
        await proxy.StopAsync(disableSystemProxy: false);
        settings.Current.ProxyPort = originalPort;
        settings.Current.ProxyMode = originalMode;
        settings.Current.ProxySelectedNode = originalNode;
        settings.Update(settings.Current);
    }
}

static async Task VerifyRuleProxy()
{
    var settings = new SettingsService();
    settings.Load();
    var profiles = new ProxyProfileService();
    if (string.IsNullOrWhiteSpace(settings.Current.ProxyProfilePath))
    {
        Console.WriteLine("Proxy profile not available.");
        return;
    }

    if (!profiles.TryLoad(settings.Current.ProxyProfilePath))
    {
        Console.WriteLine("Proxy profile could not be loaded.");
        return;
    }
    var node = profiles.GetNodeInfos()
        .FirstOrDefault(item => item.Type is "hysteria2" or "hy2");
    if (node is null)
    {
        Console.WriteLine("Hysteria2 node not found.");
        return;
    }

    var originalPort = settings.Current.ProxyPort;
    var originalMode = settings.Current.ProxyMode;
    var originalNode = settings.Current.ProxySelectedNode;
    settings.Current.ProxyPort = 17899;
    settings.Current.ProxyMode = "rule";
    settings.Current.ProxySelectedNode = node.Name;
    var proxy = new ProxyService(settings, profiles);
    var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 17899);
    blocker.Start();
    try
    {
        var status = await proxy.StartAsync();
        using var handler = new HttpClientHandler
        {
            Proxy = new System.Net.WebProxy($"http://127.0.0.1:{status.Port}"),
            UseProxy = true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        using var response = await client.GetAsync("https://www.gstatic.com/generate_204");
        Console.WriteLine(
            $"Rule proxy selected={status.SelectedNode}, port={status.Port}, " +
            $"HTTPS={(int)response.StatusCode}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Rule proxy smoke failed: {ex}");
    }
    finally
    {
        await proxy.StopAsync(disableSystemProxy: false);
        blocker.Stop();
        settings.Current.ProxyPort = originalPort;
        settings.Current.ProxyMode = originalMode;
        settings.Current.ProxySelectedNode = originalNode;
        settings.Update(settings.Current);
    }
}

static async Task HoldProxy()
{
    var settings = new SettingsService();
    settings.Load();
    var profiles = new ProxyProfileService();
    profiles.Import(settings.Current.ProxyProfilePath ?? throw new InvalidOperationException("No profile"));
    var nodes = profiles.GetNodeInfos();
    settings.Current.ProxyPort = 17897;
    settings.Current.ProxyMode = "global";
    settings.Current.ProxySelectedNode =
        nodes.FirstOrDefault(node => node.Type == "ss" && node.Supported)?.Name;
    var proxy = new ProxyService(settings, profiles);
    await proxy.StartAsync();
    Console.WriteLine("READY");
    await Task.Delay(TimeSpan.FromSeconds(30));
    await proxy.StopAsync(disableSystemProxy: false);
}

static void DumpManagement()
{
    var devices = new DeviceManagerService().GetDevices();
    Console.WriteLine($"Devices: {devices.Count}");
    foreach (var group in devices.GroupBy(device => device.Class))
    {
        Console.WriteLine($"  {group.Key}: {group.Count()}");
    }

    var startup = new TaskStartupService().GetStartupItems();
    Console.WriteLine($"Startup items: {startup.Count}");
    foreach (var item in startup.Take(8))
    {
        Console.WriteLine($"  {item.Scope}/{item.Kind}: {item.Name} ({item.Enabled})");
    }

    var tasks = new TaskStartupService().GetScheduledTasks();
    Console.WriteLine($"Scheduled tasks: {tasks.Count}");
    foreach (var task in tasks.Take(8))
    {
        Console.WriteLine($"  {task.Path} [{task.State}]");
    }
}

static void DumpInstalledApps()
{
    foreach (var app in new UninstallerService().GetInstalledApps())
    {
        Console.WriteLine(
            $"{app.Name}\t{app.Version}\t{app.Scope}/{app.RegistryView}\t" +
            $"valid={app.HasValidInstallLocation}\t{app.InstallLocation}");
    }
}

static async Task AdoptModelEngine()
{
    var settings = new SettingsService();
    settings.Load();
    using var service = new LocalModelService(settings);
    Console.WriteLine($"Before={service.GetEngineInfo()}");
    var adopted = await service.AdoptEngineAsync();
    Console.WriteLine($"After={adopted}");
}

static async Task ModelSmoke()
{
    var settings = new SettingsService();
    settings.Load();
    using var service = new LocalModelService(settings);
    var models = service.GetModels();
    var model = models.FirstOrDefault(item =>
                    item.Name.Contains("Qwen", StringComparison.OrdinalIgnoreCase)) ??
                models.OrderBy(item => item.SizeBytes).FirstOrDefault();
    if (model is null)
    {
        Console.WriteLine("No GGUF model found.");
        return;
    }

    Console.WriteLine($"Engine={service.GetEngineInfo()}");
    Console.WriteLine($"Model={model.Name} Size={model.SizeBytes}");
    var result = await service.BenchmarkAsync(model, CancellationToken.None);
    Console.WriteLine(result.Length > 4000 ? result[..4000] : result);
}

static async Task ReplacementSmoke()
{
    var displays = new BrightnessService().GetDisplays();
    var pointer = new PointerService();
    var files = new FileIndexService();
    var searchPng = files.Search("png", 20);
    var searchJson = files.Search("json", 20);
    var audio = new AudioService().GetSessions();
    var apps = new UninstallerService().GetInstalledApps();
    using var disk = new DiskAnalyzerService();
    var root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents", "Codex", "2026-11-11", "ne", "outputs", "localdeck", "src");
    disk.Start(root);
    var deadline = DateTime.UtcNow.AddSeconds(20);
    var status = disk.GetStatus();
    while (status.Running && DateTime.UtcNow < deadline)
    {
        await Task.Delay(150);
        status = disk.GetStatus();
    }
    Console.WriteLine($"Displays={displays.Count}, Pointer={pointer.GetSpeed()}");
    Console.WriteLine(
        $"SearchPng={searchPng.Count}, SearchJson={searchJson.Count}, " +
        $"FileIndex={files.GetStatus().FileCount}");
    Console.WriteLine($"AudioSessions={audio.Count}, InstalledApps={apps.Count}");
    Console.WriteLine(
        $"DiskAnalysisFiles={status.FileCount}, Bytes={status.TotalBytes}, " +
        $"Groups={status.IndexedGroups}, Error={status.Error ?? "none"}");
}

static void DisplaySmoke()
{
    var service = new BrightnessService();
    var displays = service.GetDisplays();
    foreach (var display in displays)
    {
        var writable = service.SetBrightness(display.Id, display.Current);
        Console.WriteLine(
            $"{display.Name}\t{display.Transport}\t{display.Current}%\t" +
            $"hardware={display.SupportsHardwareControl}\twrite={writable}\t" +
            $"device={display.DeviceName}\tpnp={display.PnpId}\terror={display.Error}");
    }
}

static void DisplayToggleSmoke()
{
    var service = new BrightnessService();
    var display = service.GetDisplays()
        .FirstOrDefault(item => item.Transport.StartsWith("Gamma", StringComparison.OrdinalIgnoreCase));
    if (display is null)
    {
        Console.WriteLine("Gamma display not found.");
        return;
    }

    var original = display.Current;
    var target = original == 70 ? 85 : 70;
    Console.WriteLine(
        $"{display.Name} transport={display.Transport} original={original} target={target}");
    Console.WriteLine($"Set={service.SetBrightness(display.Id, target)}");
    Console.WriteLine($"Restore={service.SetBrightness(display.Id, original)}");
}

static void FpsSmoke()
{
    using var service = new FpsMonitorService();
    service.Start();
    Thread.Sleep(2500);
    var sample = service.Sample();
    Console.WriteLine(
        $"ETW={sample.EtwAvailable}, FPS={sample.Fps}, Process={sample.ProcessName}, " +
        $"Refresh={sample.RefreshHz}, Error={sample.Error}");
}

static async Task OverlaySmoke()
{
    var system = new SystemMonitorService();
    var vendor = new VendorControlService();
    var settings = new SettingsService();
    settings.Load();
    using var service = new PerformanceOverlayService(system, vendor, settings);
    _ = await service.ReadAsync();
    await Task.Delay(1100);
    var snapshot = await service.ReadAsync();
    Console.WriteLine(
        $"CPU={snapshot.CpuPercent}% {snapshot.CpuTemperature}C, " +
        $"GPU={snapshot.GpuPercent}% {snapshot.GpuTemperature}C ({snapshot.GpuSource}), " +
        $"FPS={snapshot.Fps} process={snapshot.FpsProcess} " +
        $"refresh={snapshot.RefreshHz} ETW={snapshot.FpsAvailable}, " +
        $"batteries={snapshot.Batteries.Count}, fpsError={snapshot.FpsError}");
}

static async Task DlssDownloadSmoke()
{
    using var service = new DlssUnlockedService(() => 7897);
    var status = service.GetStatus();
    if (!status.PackageReady)
    {
        status = service.StartDownload();
        Console.WriteLine(
            $"Downloading DLSS {status.Version}: {status.Download.TotalBytes} bytes");
        var lastPercent = -1;
        while (status.Download.Running)
        {
            await Task.Delay(1000);
            status = service.GetStatus();
            var percent = (int)Math.Round(status.Download.Percent);
            if (percent >= lastPercent + 5)
            {
                Console.WriteLine(
                    $"{percent}% {status.Download.ReceivedBytes}/{status.Download.TotalBytes}");
                lastPercent = percent;
            }
        }
    }

    status = service.GetStatus();
    Console.WriteLine(
        $"Ready={status.PackageReady}, Error={status.Error ?? "none"}, Path={status.PackagePath}");
}

static async Task DlssDeploySmoke()
{
    using var service = new DlssUnlockedService(() => 7897);
    var initial = service.GetStatus();
    if (!initial.PackageReady)
    {
        Console.WriteLine("DLSS package is not downloaded. Run --dlss-download-smoke first.");
        return;
    }

    var gameRoot = Path.Combine(Path.GetTempPath(), $"localdeck-dlss-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(gameRoot);
    var gameExe = Path.Combine(gameRoot, "game.exe");
    var proxy = Path.Combine(gameRoot, "dxgi.dll");
    File.WriteAllText(gameExe, "dummy");
    File.WriteAllText(proxy, "original-proxy");
    try
    {
        service.StartInstall(gameRoot, "dxgi");
        var status = service.GetStatus();
        while (status.Busy)
        {
            await Task.Delay(500);
            status = service.GetStatus();
        }
        if (!string.IsNullOrWhiteSpace(status.Error))
        {
            throw new InvalidOperationException(status.Error);
        }
        var installation = status.Installations.FirstOrDefault(item =>
            item.GamePath.Equals(gameRoot, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Installation record was not created.");
        if (!File.Exists(proxy) || File.ReadAllText(proxy) == "original-proxy")
        {
            throw new InvalidOperationException("DLSS proxy file was not deployed.");
        }

        service.StartUninstall(installation.Id);
        status = service.GetStatus();
        while (status.Busy)
        {
            await Task.Delay(500);
            status = service.GetStatus();
        }
        if (!string.IsNullOrWhiteSpace(status.Error))
        {
            throw new InvalidOperationException(status.Error);
        }
        if (!File.Exists(gameExe) ||
            !File.Exists(proxy) ||
            File.ReadAllText(proxy) != "original-proxy")
        {
            throw new InvalidOperationException("Uninstall did not restore the original game files.");
        }
        Console.WriteLine($"DLSS deploy/rollback pass: {gameRoot}");
    }
    finally
    {
        var resolved = Path.GetFullPath(gameRoot);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }
}

static void ToggleDiagnosticTask()
{
    var service = new TaskStartupService();
    var task = service.GetScheduledTasks().FirstOrDefault(item =>
        item.Name.Equals("LocalDeckDiagnosticsTask", StringComparison.OrdinalIgnoreCase));
    if (task is null)
    {
        Console.WriteLine("Diagnostic task not found.");
        return;
    }

    Console.WriteLine($"Initial enabled={task.Enabled}");
    Console.WriteLine($"Disable result={service.SetScheduledTaskEnabled(task, false)}");
    Console.WriteLine($"Enable result={service.SetScheduledTaskEnabled(task, true)}");
}

static void ToggleDiagnosticStartup()
{
    var service = new TaskStartupService();
    var item = service.GetStartupItems().FirstOrDefault(entry =>
        entry.Name.Equals("LocalDeckDiagnosticsStartup", StringComparison.OrdinalIgnoreCase));
    if (item is null)
    {
        Console.WriteLine("Diagnostic startup item not found.");
        return;
    }

    Console.WriteLine($"Initial enabled={item.Enabled}");
    Console.WriteLine($"Disable result={service.SetStartupEnabled(item, false)}");
    Console.WriteLine($"Enable result={service.SetStartupEnabled(item, true)}");
}

static async Task DumpNativeHid()
{
    foreach (var device in DeviceList.Local.GetHidDevices(0x046D, null, null, null))
    {
        var inputLength = device.GetMaxInputReportLength();
        var outputLength = device.GetMaxOutputReportLength();
        if (device.ProductID != 0xC539 || outputLength < 7 || inputLength < 7)
        {
            continue;
        }

        Console.WriteLine(
            $"Native candidate IN={inputLength} OUT={outputLength}: {device.DevicePath}");
        using var handle = NativeHid.Open(device.DevicePath);
        if (handle.IsInvalid)
        {
            Console.WriteLine($"  open failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            continue;
        }

        var reportIds = outputLength switch
        {
            7 => new byte[] { 0x10 },
            20 => new byte[] { 0x10, 0x11 },
            _ => new byte[] { 0x11, 0x12, 0x13 }
        };
        foreach (var reportId in reportIds)
        {
            for (byte deviceIndex = 1; deviceIndex <= 2; deviceIndex++)
            {
                await NativeHid.DrainAsync(handle);
                var report = new byte[outputLength];
                report[0] = reportId;
                report[1] = deviceIndex;
                report[2] = 0x00;
                report[3] = 0x0B;
                BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(4), 0x0001);
                var write = await NativeHid.WriteAsync(handle, report);
                var response = await NativeHid.ReadAsync(handle, 128, TimeSpan.FromMilliseconds(450));
                Console.WriteLine(
                    $"  id={reportId:X2} dev={deviceIndex} write={write} rx={Convert.ToHexString(response)}");
            }
        }
    }
}

static async Task DumpHidppProbe()
{
    foreach (var device in DeviceList.Local.GetHidDevices(0x046D, null, null, null))
    {
        if (device.ProductID != 0xC539 ||
            device.GetMaxInputReportLength() < 7 ||
            device.GetMaxOutputReportLength() < 7)
        {
            continue;
        }

        Console.WriteLine(
            $"PATH={device.DevicePath} IN={device.GetMaxInputReportLength()} OUT={device.GetMaxOutputReportLength()}");
        try
        {
            using var handle = NativeHid.Open(device.DevicePath);
            if (handle.IsInvalid)
            {
                Console.WriteLine(
                    $"OPEN_ERROR={new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                continue;
            }
            foreach (var feature in new ushort[] { 0x0001, 0x0003, 0x1000, 0x1004, 0x2201, 0x2202 })
            {
                await NativeHid.DrainAsync(handle);
                var request = new byte[device.GetMaxOutputReportLength()];
                request[0] = 0x10;
                request[1] = 0x01;
                request[2] = 0x00;
                request[3] = 0x08;
                request[4] = (byte)(feature >> 8);
                request[5] = (byte)feature;
                request[0] = 0x11;
                var write = await NativeHid.WriteAsync(handle, request);
                var response = write
                    ? await NativeHid.ReadAsync(handle, 128, TimeSpan.FromMilliseconds(600))
                    : [];
                Console.WriteLine(
                    $"feature={feature:X4} write={write} " +
                    $"request={Convert.ToHexString(request)} response={Convert.ToHexString(response)}");
            }

            await NativeHid.DrainAsync(handle);
            var version = new byte[device.GetMaxOutputReportLength()];
            version[0] = 0x11;
            version[1] = 0x01;
            version[2] = 0x00;
            version[3] = 0x18;
            var versionWrite = await NativeHid.WriteAsync(handle, version);
            var versionResponse = versionWrite
                ? await NativeHid.ReadAsync(handle, 128, TimeSpan.FromMilliseconds(600))
                : [];
            Console.WriteLine(
                $"protocol write={versionWrite} response={Convert.ToHexString(versionResponse)}");

            await NativeHid.DrainAsync(handle);
            var countRequest = new byte[device.GetMaxOutputReportLength()];
            countRequest[0] = 0x11;
            countRequest[1] = 0x01;
            countRequest[2] = 0x01;
            countRequest[3] = 0x08;
            if (await NativeHid.WriteAsync(handle, countRequest))
            {
                var countResponse = await NativeHid.ReadAsync(
                    handle, 128, TimeSpan.FromMilliseconds(600));
                Console.WriteLine($"featureCount={Convert.ToHexString(countResponse)}");
                var count = countResponse.Length > 4 ? countResponse[4] : (byte)0;
                for (byte index = 0; index < count; index++)
                {
                    await NativeHid.DrainAsync(handle);
                    var itemRequest = new byte[device.GetMaxOutputReportLength()];
                    itemRequest[0] = 0x11;
                    itemRequest[1] = 0x01;
                    itemRequest[2] = 0x01;
                    itemRequest[3] = 0x18;
                    itemRequest[4] = index;
                    if (!await NativeHid.WriteAsync(handle, itemRequest))
                    {
                        continue;
                    }
                    var itemResponse = await NativeHid.ReadAsync(
                        handle, 128, TimeSpan.FromMilliseconds(400));
                    if (itemResponse.Length >= 6)
                    {
                        Console.WriteLine(
                            $"featureIndex={index:X2} id={itemResponse[4]:X2}{itemResponse[5]:X2}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR={ex.GetType().Name}: {ex.Message}");
        }
    }
}

static void MacroSmoke()
{
    var settings = new SettingsService();
    settings.Load();
    var resultFile = Path.GetFullPath(@"..\..\work\macro-smoke.txt");
    File.Delete(resultFile);
    var macroId = "diagnostic-macro";
    settings.Current.MouseMacrosEnabled = true;
    settings.Current.Macros =
    [
        new LocalDeck.App.Models.MacroDefinition
        {
            Id = macroId,
            Name = "Diagnostic",
            Enabled = true,
            Steps =
            [
                new LocalDeck.App.Models.MacroStep
                {
                    Type = "run",
                    Value = $"cmd.exe /c echo MACRO_OK>{resultFile}"
                }
            ]
        }
    ];
    settings.Current.MacroBindings["MouseX1"] = macroId;

    using var ready = new ManualResetEventSlim();
    var macroService = default(MacroService);
    var thread = new Thread(() =>
    {
        macroService = new MacroService(settings);
        macroService.Start();
        ready.Set();
        Dispatcher.Run();
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    ready.Wait(TimeSpan.FromSeconds(3));
    Thread.Sleep(250);
    SyntheticMouse.SendXButton(1);
    var deadline = DateTime.UtcNow.AddSeconds(4);
    while (!File.Exists(resultFile) && DateTime.UtcNow < deadline)
    {
        Thread.Sleep(100);
    }
    Console.WriteLine($"Macro hook installed={macroService?.HookInstalled}");
    Console.WriteLine($"Macro result exists={File.Exists(resultFile)}");
    if (File.Exists(resultFile))
    {
        Console.WriteLine($"Macro result={File.ReadAllText(resultFile).Trim()}");
    }
    Dispatcher.FromThread(thread)?.InvokeShutdown();
    thread.Join(TimeSpan.FromSeconds(2));
}

static async Task DumpGeneralTools()
{
    var settings = new SettingsService();
    settings.Load();
    var apps = new UninstallerService().GetInstalledApps();
    var drivers = new DriverService().GetInstalledDrivers();
    var executables = new ExecutableCatalogService().GetExecutables("", 1200);
    using var modelService = new LocalModelService(settings);
    var models = modelService.GetModels();
    var engine = modelService.GetEngineInfo();
    var logs = await new WindowsLogService().AnalyzeAsync(TimeSpan.FromHours(1), 80);
    Console.WriteLine($"Apps={apps.Count}, Drivers={drivers.Count}, Executables={executables.Count}, Models={models.Count}");
    Console.WriteLine(
        $"ExeGroups=game:{executables.Count(item => item.Kind == "game")}, " +
        $"application:{executables.Count(item => item.Kind == "application")}, " +
        $"utility:{executables.Count(item => item.Kind == "utility")}");
    Console.WriteLine(
        $"FirstExecutables={string.Join(", ", executables.Take(8).Select(item => $"{item.Kind}:{item.Name}"))}");
    Console.WriteLine($"Logs={logs.Entries.Count}, Providers={logs.Groups.Count}, Minidumps={logs.Minidumps.Count}");
    Console.WriteLine(
        $"AppRegistry32={apps.Count(item => item.RegistryPath.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase))}, " +
        $"EngineServer={engine.ServerAvailable}, EngineBench={engine.BenchmarkAvailable}, EngineOwned={engine.OwnedByLocalDeck}");
    using var connection = new SqliteConnection($"Data Source={AppPaths.DatabasePath};Mode=ReadOnly");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT (SELECT COUNT(*) FROM exe_catalog),
               (SELECT COUNT(*) FROM files WHERE lower(name) LIKE '%.exe');
        """;
    using var reader = command.ExecuteReader();
    if (reader.Read())
    {
        Console.WriteLine($"CatalogExe={reader.GetInt64(0)}, IndexedExe={reader.GetInt64(1)}");
    }
}

static class NativeHid
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    public static SafeFileHandle Open(string path) =>
        CreateFileW(path, GenericRead | GenericWrite, ShareRead | ShareWrite, IntPtr.Zero,
            OpenExisting, 0, IntPtr.Zero);

    public static Task<bool> WriteAsync(SafeFileHandle handle, byte[] data) =>
        Task.Run(() => WriteFile(handle, data, (uint)data.Length, out var written, IntPtr.Zero) &&
                       written == data.Length);

    public static async Task<byte[]> ReadAsync(
        SafeFileHandle handle, int length, TimeSpan timeout)
    {
        var buffer = new byte[length];
        var readTask = Task.Run(() =>
        {
            if (!ReadFile(handle, buffer, (uint)buffer.Length, out var read, IntPtr.Zero))
            {
                return Array.Empty<byte>();
            }
            return buffer.AsSpan(0, (int)read).ToArray();
        });
        if (await Task.WhenAny(readTask, Task.Delay(timeout)) != readTask)
        {
            CancelIoEx(handle, IntPtr.Zero);
            try
            {
                await readTask;
            }
            catch
            {
                // Cancellation completes the blocked read.
            }
            return [];
        }
        return await readTask;
    }

    public static async Task DrainAsync(SafeFileHandle handle)
    {
        while (true)
        {
            var data = await ReadAsync(handle, 64, TimeSpan.FromMilliseconds(25));
            if (data.Length == 0)
            {
                return;
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle file, byte[] buffer, uint bytesToWrite, out uint bytesWritten,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle file, byte[] buffer, uint bytesToRead, out uint bytesRead,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
}

static class SyntheticMouse
{
    public static void SendXButton(int button)
    {
        mouse_event(0x0080, 0, 0, (uint)button, UIntPtr.Zero);
        mouse_event(0x0100, 0, 0, (uint)button, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
