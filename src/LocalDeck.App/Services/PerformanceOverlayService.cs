namespace LocalDeck.App.Services;

public sealed class PerformanceOverlayService : IDisposable
{
    private readonly SystemMonitorService _system;
    private readonly VendorControlService _vendor;
    private readonly SettingsService _settings;
    private readonly BluetoothBatteryService _bluetooth = new();
    private readonly GpuTelemetryService _gpu = new();
    private readonly FpsMonitorService _fps = new();
    private readonly object _batterySync = new();
    private IReadOnlyList<OverlayBattery> _batteries = [];
    private DateTime _batteryUpdated = DateTime.MinValue;
    private int _batteryReading;

    public PerformanceOverlayService(
        SystemMonitorService system, VendorControlService vendor, SettingsService settings)
    {
        _system = system;
        _vendor = vendor;
        _settings = settings;
    }

    public async Task<PerformanceOverlaySnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var system = _system.ReadLite();
        var vendor = _vendor.ReadTelemetry();
        var gpu = _gpu.Read();
        var fps = _fps.Sample(_settings.Current.PerformanceOverlayFpsSource);

        await RefreshBatteriesIfNeededAsync(cancellationToken);
        OverlayBattery[] batteries;
        lock (_batterySync)
        {
            batteries = _batteries.ToArray();
        }

        var memoryPercent = system.MemoryTotalBytes == 0
            ? 0
            : system.MemoryUsedBytes * 100d / system.MemoryTotalBytes;
        var diskTotal = system.Disks.Sum(item => item.TotalBytes);
        var diskFree = system.Disks.Sum(item => item.FreeBytes);
        var diskPercent = diskTotal == 0
            ? 0
            : (diskTotal - diskFree) * 100d / diskTotal;
        return new PerformanceOverlaySnapshot(
            Math.Round(system.CpuPercent, 1),
            vendor.CpuTemperature,
            gpu.UsagePercent is null ? null : Math.Round(gpu.UsagePercent.Value, 1),
            gpu.TemperatureC ?? vendor.GpuTemperature,
            vendor.CpuFanRpm,
            vendor.GpuFanRpm,
            gpu.MemoryPercent,
            Math.Round(memoryPercent, 1),
            system.DownloadBytesPerSecond,
            system.UploadBytesPerSecond,
            system.BatteryPercent,
            system.BatteryCharging,
            Math.Round(diskPercent, 1),
            system.Uptime,
            fps.Fps,
            fps.ProcessName,
            fps.RefreshHz,
            fps.EtwAvailable,
            fps.Error,
            gpu.Name,
            gpu.Source,
            batteries,
            _settings.Current.PerformanceOverlayOpacity,
            _settings.Current.PerformanceOverlayMetrics.ToArray(),
            DateTimeOffset.Now);
    }

    public bool LaunchElevatedFpsHelper() => _fps.LaunchElevatedHelper();

    public void Dispose()
    {
        _gpu.Dispose();
        _fps.Dispose();
    }

    private async Task RefreshBatteriesIfNeededAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _batteryUpdated < TimeSpan.FromSeconds(15) ||
            Interlocked.CompareExchange(ref _batteryReading, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var devices = await _bluetooth.GetConnectedBatteryLevelsAsync(cancellationToken);
            lock (_batterySync)
            {
                _batteries = devices
                    .Select(item => new OverlayBattery(
                        item.Name, item.BatteryPercent, "蓝牙"))
                    .ToArray();
                _batteryUpdated = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Overlay Bluetooth battery read failed: {ex.Message}");
            lock (_batterySync)
            {
                _batteryUpdated = DateTime.UtcNow;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _batteryReading, 0);
        }
    }
}

public sealed record PerformanceOverlaySnapshot(
    double CpuPercent,
    int? CpuTemperature,
    double? GpuPercent,
    int? GpuTemperature,
    int? CpuFanRpm,
    int? GpuFanRpm,
    double? GpuMemoryPercent,
    double MemoryPercent,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    int? SystemBatteryPercent,
    bool SystemBatteryCharging,
    double DiskUsedPercent,
    TimeSpan Uptime,
    double? Fps,
    string? FpsProcess,
    int? RefreshHz,
    bool FpsAvailable,
    string? FpsError,
    string GpuName,
    string GpuSource,
    IReadOnlyList<OverlayBattery> Batteries,
    double Opacity,
    IReadOnlyList<string> Metrics,
    DateTimeOffset Timestamp);

public sealed record OverlayBattery(
    string Name,
    int Percent,
    string Kind);
