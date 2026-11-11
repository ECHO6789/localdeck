using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LocalDeck.App.Bridge;
using LocalDeck.App.Models;
using LocalDeck.App.Services;
using LocalDeck.App.Windows;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace LocalDeck.App;

public partial class MainWindow : Window
{
    private const int WmClipboardUpdate = 0x031D;
    private const int WmHotkey = 0x0312;

    private readonly SettingsService _settings = new();
    private readonly SystemMonitorService _system = new();
    private readonly BrightnessService _brightness = new();
    private readonly VendorControlService _vendor = new();
    private readonly ProxyProfileService _proxyProfiles = new();
    private readonly ProxyService _proxy;
    private readonly LogitechHidService _logitech = new();
    private readonly PointerService _pointer = new();
    private readonly ClipboardService _clipboard;
    private readonly FileIndexService _files = new();
    private readonly AudioService _audio = new();
    private readonly ScreenshotService _screenshot = new();
    private readonly HotkeyService _hotkeys = new();
    private readonly MacroService _macros;
    private readonly DispatcherTimer _clipboardTimer;
    private readonly DispatcherTimer _overlaySettingsSaveTimer;
    private BridgeDispatcher? _bridge;
    private TrayService? _tray;
    private MacroWindow? _macroWindow;
    private PerformanceOverlayWindow? _performanceOverlayWindow;
    private PerformanceOverlayService? _performanceOverlayService;
    private HwndSource? _source;
    private bool _exitRequested;
    private bool _screenshotInProgress;
    private bool _webReady;

    public MainWindow()
    {
        InitializeComponent();
        _settings.Load();
        _proxy = new ProxyService(_settings, _proxyProfiles);
        _macros = new MacroService(_settings);
        EnsureLaunchAtLogin();
        _clipboard = new ClipboardService();
        _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _clipboardTimer.Tick += OnClipboardTimerTick;
        _overlaySettingsSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _overlaySettingsSaveTimer.Tick += (_, _) =>
        {
            _overlaySettingsSaveTimer.Stop();
            _settings.Save();
        };
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        _proxy.ApplySettings();
        if (!_settings.Current.ProxySystemProxyEnabled)
        {
            _proxy.DisableOwnedSystemProxy();
        }
        if (string.IsNullOrWhiteSpace(_settings.Current.ProxySelectedNode))
        {
            var selectedNode = _proxy.GetStatus().Nodes.FirstOrDefault(node => node.Supported);
            if (selectedNode is not null)
            {
                _settings.Current.ProxySelectedNode = selectedNode.Name;
                _settings.Save();
            }
        }
        _hotkeys.Reload(handle, _settings.Current);
        if (IsPluginEnabled("automation"))
        {
            _macros.Start();
        }
        _settings.Changed += OnSettingsChanged;
        AddClipboardFormatListener(handle);

        _tray = new TrayService(
            this,
            OpenClipboard,
            CaptureScreenshotFromTray,
            () => _ = ExecuteHotkeyAsync("proxyToggle", ""),
            () => _ = TogglePerformanceOverlay(),
            ExitApplication);

        _bridge = new BridgeDispatcher(
            this, _settings, _system, _brightness, _vendor, _proxy, _proxyProfiles,
            _logitech, _pointer, _clipboard, _files, _audio, _screenshot,
            _hotkeys, _macros);
        _bridge.OpenMacroEditorWindow = OpenMacroEditorWindow;
        _bridge.TogglePerformanceOverlay = TogglePerformanceOverlay;
        _bridge.UpdatePerformanceOverlay = UpdatePerformanceOverlaySettings;

        await InitializeWebViewAsync();

        if (_files.GetStatus().FileCount == 0 && !_files.GetStatus().Indexing)
        {
            _files.StartIndex(_settings.Current.SearchRoots);
        }

        if (_settings.Current.ProxyAutoStart &&
            !string.IsNullOrWhiteSpace(_settings.Current.ProxyProfilePath))
        {
            try
            {
                await _proxy.StartAsync();
                if (_settings.Current.ProxySystemProxyEnabled)
                {
                    _proxy.SetSystemProxy(true);
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Proxy auto-start failed: {ex.Message}");
            }
        }

        if (_settings.Current.PerformanceOverlayEnabled && IsPluginEnabled("hardware"))
        {
            ShowPerformanceOverlay();
        }

        if (Environment.GetCommandLineArgs().Contains("--minimized"))
        {
            Hide();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            var core = WebView.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.IsWebMessageEnabled = true;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.WebMessageReceived += OnWebMessageReceived;

            var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!File.Exists(Path.Combine(webRoot, "index.html")))
            {
                throw new FileNotFoundException(
                    "前端资源未生成。请先运行 scripts/build.ps1 或 npm run build。", webRoot);
            }

            core.SetVirtualHostNameToFolderMapping(
                "localdeck.app", webRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate("https://localdeck.app/index.html");
            _webReady = true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"WebView initialization failed: {ex}");
            MessageBox.Show(
                $"无法启动界面：{ex.Message}",
                "LocalDeck",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_bridge is null)
        {
            return;
        }

        try
        {
            var response = await _bridge.DispatchAsync(e.WebMessageAsJson);
            WebView.CoreWebView2.PostWebMessageAsJson(response);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Web message response failed: {ex}");
        }
    }

    private IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate)
        {
            _clipboardTimer.Stop();
            _clipboardTimer.Start();
            handled = true;
        }
        else if (message == WmHotkey)
        {
            handled = true;
            if (_hotkeys.TryGetAction(wParam.ToInt32(), out var hotkey))
            {
                _ = ExecuteHotkeyAsync(hotkey.Action, hotkey.Arguments);
            }
        }

        return IntPtr.Zero;
    }

    private void OnClipboardTimerTick(object? sender, EventArgs e)
    {
        _clipboardTimer.Stop();
        _clipboard.CaptureImages = _settings.Current.ClipboardCaptureImages;
        _clipboard.CaptureCurrentClipboard();
        NotifyEvent("clipboard-updated");
    }

    private void OpenClipboard()
    {
        if (!_webReady)
        {
            return;
        }

        ShowWindowAndNotify("show-clipboard");
    }

    private void ShowWindowAndNotify(string action)
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        NotifyEvent(action);
    }

    private void NotifyEvent(string action)
    {
        if (!_webReady || WebView.CoreWebView2 is null)
        {
            return;
        }

        WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "event",
            action,
            at = DateTimeOffset.Now
        }));
    }

    private void CaptureScreenshotFromTray()
    {
        _ = CaptureScreenshotFromHotkeyAsync(pinAfterCapture: true);
    }

    private async Task CaptureScreenshotFromHotkeyAsync(bool pinAfterCapture = false)
    {
        if (_screenshotInProgress)
        {
            return;
        }

        _screenshotInProgress = true;
        try
        {
            var result = await _screenshot.CaptureRegionAsync(this, pinAfterCapture);
            if (result?.Path is not null)
            {
                NotifyEvent("screenshot-captured");
            }
        }
        finally
        {
            _screenshotInProgress = false;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray)
        {
            Hide();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitRequested && _settings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _clipboardTimer.Stop();
        _overlaySettingsSaveTimer.Stop();
        _settings.Save();
        _settings.Changed -= OnSettingsChanged;
        _hotkeys.Dispose();
        _macros.Dispose();
        try
        {
            _proxy.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Process exit will release the loopback listener as a final fallback.
        }
        _bridge?.Dispose();
        _tray?.Dispose();
        _performanceOverlayWindow?.Close();
        _performanceOverlayService?.Dispose();
        if (_source is not null)
        {
            RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
        }
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _tray?.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    private async Task ExecuteHotkeyAsync(string action, string arguments)
    {
        switch (action)
        {
            case "showClipboard":
                OpenClipboard();
                break;
            case "search":
                ShowWindowAndNotify("focus-search");
                break;
            case "showPanel":
                ShowWindowAndNotify("show-panel");
                break;
            case "screenshot":
                await CaptureScreenshotFromHotkeyAsync(pinAfterCapture: false);
                break;
            case "screenshotPin":
                await CaptureScreenshotFromHotkeyAsync(pinAfterCapture: true);
                break;
            case "pinImage":
                if (!_screenshot.PinClipboardImage())
                {
                    NotifyEvent("pin-image-empty");
                }
                break;
            case "proxyToggle":
                if (_proxy.IsRunning)
                {
                    await _proxy.StopAsync();
                }
                else
                {
                    await _proxy.StartAsync();
                }
                NotifyEvent("proxy-updated");
                break;
            case "macro":
                if (!IsPluginEnabled("automation"))
                {
                    ShowWindowAndNotify("plugin-required");
                    break;
                }
                _macros.RunMacro(arguments);
                break;
            case "launch":
                _macros.RunCommand(arguments);
                break;
            case "text":
                _macros.TypeText(arguments);
                break;
            case "lock":
                LockWorkStation();
                break;
            case "openMacro":
                if (!OpenMacroEditorWindow())
                {
                    ShowWindowAndNotify("plugin-required");
                }
                break;
            case "showLauncher":
                ShowWindowAndNotify("show-launcher");
                break;
            case "overlayToggle":
                if (!IsPluginEnabled("hardware"))
                {
                    ShowWindowAndNotify("plugin-required");
                }
                else
                {
                    TogglePerformanceOverlay();
                }
                break;
        }
    }

    private bool TogglePerformanceOverlay()
    {
        if (!IsPluginEnabled("hardware"))
        {
            NotifyEvent("plugin-required");
            return false;
        }
        if (_performanceOverlayWindow is { IsVisible: true })
        {
            _performanceOverlayWindow.Hide();
            _settings.Current.PerformanceOverlayEnabled = false;
            _settings.Update(_settings.Current);
            return false;
        }

        ShowPerformanceOverlay();
        return true;
    }

    private void ShowPerformanceOverlay()
    {
        _performanceOverlayService ??=
            new PerformanceOverlayService(_system, _vendor, _settings);
        if (_performanceOverlayWindow is null)
        {
            _performanceOverlayWindow = new PerformanceOverlayWindow(
                _performanceOverlayService,
                SaveOverlayPosition,
                SaveOverlaySize,
                () =>
                {
                    _settings.Current.PerformanceOverlayEnabled = false;
                    _settings.Update(_settings.Current);
                });
        }
        _performanceOverlayWindow.ApplyOptions(_settings.Current);

        var workArea = SystemParameters.WorkArea;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        _performanceOverlayWindow.Left = Math.Clamp(
            _settings.Current.PerformanceOverlayLeft ??
                workArea.Right - _performanceOverlayWindow.Width - 8,
            virtualLeft,
            Math.Max(virtualLeft, virtualRight - _performanceOverlayWindow.Width));
        _performanceOverlayWindow.Top = Math.Clamp(
            _settings.Current.PerformanceOverlayTop ?? workArea.Top + 22,
            virtualTop,
            Math.Max(
                virtualTop,
                virtualBottom - _performanceOverlayWindow.Height));
        _performanceOverlayWindow.Show();
        _performanceOverlayWindow.Topmost = true;
        if (!_settings.Current.PerformanceOverlayEnabled)
        {
            _settings.Current.PerformanceOverlayEnabled = true;
            _settings.Update(_settings.Current);
        }
    }

    private void SaveOverlayPosition(double left, double top)
    {
        if (_performanceOverlayWindow is null)
        {
            return;
        }
        _settings.Current.PerformanceOverlayLeft = left;
        _settings.Current.PerformanceOverlayTop = top;
        _settings.Save();
    }

    private void SaveOverlaySize(double width, double height)
    {
        _settings.Current.PerformanceOverlayWidth = Math.Clamp(width, 220, 800);
        _settings.Current.PerformanceOverlayHeight = Math.Clamp(height, 180, 900);
        ScheduleOverlaySettingsSave();
        NotifyEvent("performance-overlay-updated");
    }

    private AppSettings UpdatePerformanceOverlaySettings(
        PerformanceOverlaySettingsUpdate update)
    {
        var settings = _settings.Current;
        if (update.Enabled is not null)
        {
            settings.PerformanceOverlayEnabled = update.Enabled.Value;
        }
        if (update.Opacity is not null)
        {
            settings.PerformanceOverlayOpacity = Math.Clamp(update.Opacity.Value, 0.3, 1);
        }
        if (!string.IsNullOrWhiteSpace(update.FpsSource))
        {
            settings.PerformanceOverlayFpsSource = update.FpsSource;
        }
        if (update.Metrics is not null)
        {
            settings.PerformanceOverlayMetrics = update.Metrics
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        if (update.Width is not null)
        {
            settings.PerformanceOverlayWidth = Math.Clamp(update.Width.Value, 220, 800);
        }
        if (update.Height is not null)
        {
            settings.PerformanceOverlayHeight = Math.Clamp(update.Height.Value, 180, 900);
        }

        if (settings.PerformanceOverlayEnabled)
        {
            if (_performanceOverlayWindow is not { IsVisible: true })
            {
                ShowPerformanceOverlay();
            }
            _performanceOverlayWindow?.ApplyOptions(settings);
        }
        else
        {
            _performanceOverlayWindow?.Hide();
        }

        ScheduleOverlaySettingsSave();
        NotifyEvent("performance-overlay-updated");
        return settings;
    }

    private void ScheduleOverlaySettingsSave()
    {
        _overlaySettingsSaveTimer.Stop();
        _overlaySettingsSaveTimer.Start();
    }

    private bool OpenMacroEditorWindow()
    {
        if (!IsPluginEnabled("automation"))
        {
            return false;
        }
        if (_bridge is null)
        {
            return false;
        }
        if (_macroWindow is { IsVisible: true })
        {
            _macroWindow.Activate();
            return true;
        }

        _macroWindow = new MacroWindow(_bridge.DispatchAsync) { Owner = this };
        _macroWindow.Closed += (_, _) => _macroWindow = null;
        _macroWindow.Show();
        return true;
    }

    private void OnSettingsChanged(Models.AppSettings settings)
    {
        EnsureLaunchAtLogin();
        _proxy.ApplySettings();
        if (_source is not null)
        {
            _hotkeys.Reload(_source.Handle, settings);
        }
        if (IsPluginEnabled("automation"))
        {
            _macros.Start();
        }
        else
        {
            _macros.Stop();
        }
        if (!IsPluginEnabled("hardware"))
        {
            _performanceOverlayWindow?.Hide();
        }
        else if (settings.PerformanceOverlayEnabled &&
                 _performanceOverlayWindow is not { IsVisible: true })
        {
            ShowPerformanceOverlay();
        }
        else if (settings.PerformanceOverlayEnabled)
        {
            _performanceOverlayWindow?.ApplyOptions(settings);
        }
        else
        {
            _performanceOverlayWindow?.Hide();
        }
        NotifyEvent("settings-updated");
    }

    private void EnsureLaunchAtLogin()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null)
            {
                return;
            }

            if (_settings.Current.LaunchAtLogin)
            {
                key.SetValue("LocalDeck", $"\"{Environment.ProcessPath}\" --minimized");
            }
            else
            {
                key.DeleteValue("LocalDeck", throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Startup registration failed: {ex.Message}");
        }
    }

    private bool IsPluginEnabled(string id) =>
        _settings.Current.EnabledPlugins.Contains(id, StringComparer.OrdinalIgnoreCase);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();
}
