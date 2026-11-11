using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using LocalDeck.App.Models;
using LocalDeck.App.Services;

namespace LocalDeck.App.Windows;

public partial class PerformanceOverlayWindow : Window
{
    private static readonly (string Key, string Label, string Tone)[] MetricDefinitions =
    [
        ("cpuUsage", "CPU", "#61E7B8"),
        ("cpuTemp", "CPU 温度", "#61E7B8"),
        ("cpuFan", "CPU 风扇", "#61E7B8"),
        ("gpuUsage", "GPU", "#FFAD66"),
        ("gpuTemp", "GPU 温度", "#FFAD66"),
        ("gpuFan", "GPU 风扇", "#FFAD66"),
        ("gpuMemory", "显存", "#FFAD66"),
        ("memory", "内存", "#6BB8FF"),
        ("networkDown", "下载", "#6BB8FF"),
        ("networkUp", "上传", "#6BB8FF"),
        ("fps", "FPS", "#6BB8FF"),
        ("battery", "系统电池", "#BB8CFF"),
        ("disk", "磁盘", "#BB8CFF"),
        ("uptime", "运行时间", "#8493A6")
    ];

    private readonly PerformanceOverlayService _metrics;
    private readonly Action<double, double> _savePosition;
    private readonly Action<double, double> _saveSize;
    private readonly Action _hideOverlay;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _saveSizeTimer;
    private Brush _cardBackground = Brush("#8017212A");
    private PerformanceOverlaySnapshot? _lastSnapshot;
    private bool _refreshing;

    public PerformanceOverlayWindow(
        PerformanceOverlayService metrics,
        Action<double, double> savePosition,
        Action<double, double> saveSize,
        Action hideOverlay)
    {
        InitializeComponent();
        _metrics = metrics;
        _savePosition = savePosition;
        _saveSize = saveSize;
        _hideOverlay = hideOverlay;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _saveSizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveSizeTimer.Tick += (_, _) =>
        {
            _saveSizeTimer.Stop();
            _saveSize(Width, Height);
        };
        Loaded += (_, _) =>
        {
            Topmost = true;
            _timer.Start();
            _ = RefreshAsync();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void ApplyOptions(AppSettings settings)
    {
        Width = Math.Clamp(settings.PerformanceOverlayWidth, 220, 800);
        Height = Math.Clamp(settings.PerformanceOverlayHeight, 180, 900);
        ApplyBackgrounds(settings.PerformanceOverlayOpacity);
        if (_lastSnapshot is not null)
        {
            _lastSnapshot = _lastSnapshot with
            {
                Opacity = settings.PerformanceOverlayOpacity,
                Metrics = settings.PerformanceOverlayMetrics.ToArray()
            };
            ApplyMetrics(_lastSnapshot);
        }
        _ = RefreshAsync();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }
        _refreshing = true;
        try
        {
            var snapshot = await _metrics.ReadAsync();
            ApplyBackgrounds(snapshot.Opacity);
            ApplyMetrics(snapshot);
            BatteryPanel.Visibility = snapshot.Metrics.Contains(
                "bluetooth", StringComparer.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
            BatteryList.Text = snapshot.Batteries.Count == 0
                ? "未发现带标准电量服务的已连接蓝牙设备"
                : string.Join("\n", snapshot.Batteries.Select(item =>
                    $"{item.Name}    {item.Percent}%  ·  {item.Kind}"));
            GpuSource.Text = $"GPU · {snapshot.GpuSource}";
            UpdatedAt.Text = snapshot.Timestamp.ToString("HH:mm:ss");
            Topmost = true;
        }
        catch (Exception ex)
        {
            UpdatedAt.Text = ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyMetrics(PerformanceOverlaySnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        MetricWrap.Children.Clear();
        var values = new Dictionary<string, (string Value, string Detail, double? Progress)>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["cpuUsage"] = ($"{snapshot.CpuPercent:0}%", "处理器", snapshot.CpuPercent),
            ["cpuTemp"] = (
                snapshot.CpuTemperature is null ? "--" : $"{snapshot.CpuTemperature}°C",
                "温度", null),
            ["cpuFan"] = (
                snapshot.CpuFanRpm is null ? "--" : $"{snapshot.CpuFanRpm}",
                "RPM", null),
            ["gpuUsage"] = (
                snapshot.GpuPercent is null ? "--" : $"{snapshot.GpuPercent:0}%",
                "图形处理器", snapshot.GpuPercent),
            ["gpuTemp"] = (
                snapshot.GpuTemperature is null ? "--" : $"{snapshot.GpuTemperature}°C",
                "温度", null),
            ["gpuFan"] = (
                snapshot.GpuFanRpm is null ? "--" : $"{snapshot.GpuFanRpm}",
                "RPM", null),
            ["gpuMemory"] = (
                snapshot.GpuMemoryPercent is null ? "--" : $"{snapshot.GpuMemoryPercent:0}%",
                "显存占用", snapshot.GpuMemoryPercent),
            ["memory"] = ($"{snapshot.MemoryPercent:0}%", "物理内存", snapshot.MemoryPercent),
            ["networkDown"] = (
                FormatRate(snapshot.DownloadBytesPerSecond), "下载", null),
            ["networkUp"] = (
                FormatRate(snapshot.UploadBytesPerSecond), "上传", null),
            ["fps"] = (
                snapshot.Fps is not null
                    ? $"{snapshot.Fps:0}"
                    : snapshot.RefreshHz is not null ? $"{snapshot.RefreshHz}" : "--",
                snapshot.Fps is not null
                    ? snapshot.FpsProcess ?? "前台程序"
                    : snapshot.RefreshHz is not null ? "刷新率 Hz" : "无 Present 数据",
                null),
            ["battery"] = (
                snapshot.SystemBatteryPercent is null
                    ? "--"
                    : $"{snapshot.SystemBatteryPercent}%",
                snapshot.SystemBatteryCharging ? "充电中" : "系统电池",
                snapshot.SystemBatteryPercent),
            ["disk"] = ($"{snapshot.DiskUsedPercent:0}%", "固定磁盘占用", snapshot.DiskUsedPercent),
            ["uptime"] = (
                $"{(int)snapshot.Uptime.TotalDays}天",
                $"{snapshot.Uptime.Hours:D2}:{snapshot.Uptime.Minutes:D2}",
                null)
        };

        foreach (var definition in MetricDefinitions)
        {
            if (!snapshot.Metrics.Contains(definition.Key, StringComparer.OrdinalIgnoreCase) ||
                !values.TryGetValue(definition.Key, out var value))
            {
                continue;
            }
            MetricWrap.Children.Add(CreateMetricCard(
                definition.Label,
                value.Value,
                value.Detail,
                value.Progress,
                definition.Tone,
                CalculateCardWidth()));
        }

        if (MetricWrap.Children.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "请在 LocalDeck 设置中选择悬浮窗指标",
                Foreground = Brush("#8493A6"),
                FontSize = 10,
                Margin = new Thickness(4, 8, 4, 8)
            };
            MetricWrap.Children.Add(empty);
        }

        PreciseFpsButton.Visibility =
            snapshot.Metrics.Contains("fps", StringComparer.OrdinalIgnoreCase) &&
            !snapshot.FpsAvailable
                ? Visibility.Visible
                : Visibility.Collapsed;
        PreciseFpsButton.ToolTip = snapshot.FpsError ??
            "精确 FPS 需要管理员权限启动本地 ETW 采集助手。";
    }

    private Border CreateMetricCard(
        string label,
        string value,
        string detail,
        double? progress,
        string tone,
        double width)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush("#8493A6"),
            FontSize = 8,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        content.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = Brush(tone),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        content.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = Brush("#667588"),
            FontSize = 8,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (progress is not null)
        {
            content.Children.Add(new ProgressBar
            {
                Height = 2,
                Margin = new Thickness(0, 5, 0, 0),
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp(progress.Value, 0, 100),
                Foreground = Brush(tone),
                Background = Brush("#1D2934")
            });
        }

        return new Border
        {
            Width = width,
            MinHeight = 64,
            Margin = new Thickness(2),
            Padding = new Thickness(8, 7, 8, 6),
            CornerRadius = new CornerRadius(9),
            Background = _cardBackground,
            Child = content
        };
    }

    private double CalculateCardWidth()
    {
        var available = MetricWrap.ActualWidth;
        if (available <= 0)
        {
            available = Math.Max(120, ActualWidth - 42);
        }
        var columns = available switch
        {
            < 250 => 2,
            < 390 => 3,
            < 520 => 4,
            _ => 5
        };
        return Math.Max(74, Math.Floor(available / columns) - 4);
    }

    private void ApplyBackgrounds(double opacity)
    {
        var value = Math.Clamp(opacity, 0.3, 1);
        OverlaySurface.Background = new SolidColorBrush(
            Color.FromArgb((byte)Math.Round(240 * value), 0x0B, 0x11, 0x18));
        _cardBackground = new SolidColorBrush(
            Color.FromArgb((byte)Math.Round(128 * value), 0x17, 0x21, 0x2A));
        BatteryPanel.Background = _cardBackground;
    }

    private static string FormatRate(double bytes) =>
        bytes >= 1024d * 1024d
            ? $"{bytes / 1024d / 1024d:0.0}M/s"
            : $"{bytes / 1024d:0}K/s";

    private static Brush Brush(string color) =>
        (Brush)new BrushConverter().ConvertFromString(color)!;

    private async void OnPreciseFpsClicked(object sender, RoutedEventArgs e)
    {
        if (!_metrics.LaunchElevatedFpsHelper())
        {
            UpdatedAt.Text = "管理员授权失败";
            return;
        }
        UpdatedAt.Text = "FPS Helper 启动中";
        await Task.Delay(1200);
        await RefreshAsync();
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        try
        {
            DragMove();
            _savePosition(Left, Top);
        }
        catch
        {
            // DragMove can be interrupted by display topology changes.
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastSnapshot is not null)
        {
            ApplyMetrics(_lastSnapshot);
        }
        if (!IsVisible)
        {
            return;
        }
        _saveSizeTimer.Stop();
        _saveSizeTimer.Start();
    }

    private void OnResizeGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        ReleaseCapture();
        SendMessage(
            new WindowInteropHelper(this).Handle,
            WmNcLButtonDown,
            new IntPtr(HtBottomRight),
            IntPtr.Zero);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Hide();
        _hideOverlay();
    }

    private const int WmNcLButtonDown = 0x00A1;
    private const int HtBottomRight = 17;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
