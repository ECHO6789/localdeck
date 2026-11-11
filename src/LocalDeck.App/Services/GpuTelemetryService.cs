using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalDeck.App.Services;

public sealed class GpuTelemetryService : IDisposable
{
    private readonly object _sync = new();
    private IntPtr _device;
    private bool _initialized;
    private bool _nvmlUnavailable;
    private DateTime _cachedAt = DateTime.MinValue;
    private GpuTelemetry _cached = new(null, null, null, "未初始化", "未采样", null);
    private PerformanceCounter[] _fallbackCounters = [];
    private DateTime _counterRefresh = DateTime.MinValue;
    private string? _error;

    public GpuTelemetry Read()
    {
        lock (_sync)
        {
            if (DateTime.UtcNow - _cachedAt < TimeSpan.FromMilliseconds(800))
            {
                return _cached;
            }

            var utilization = ReadNvml();
            if (utilization is not null)
            {
                _cached = utilization;
                _cachedAt = DateTime.UtcNow;
                return utilization;
            }

            var fallback = ReadWindowsCounters();
            _cached = fallback;
            _cachedAt = DateTime.UtcNow;
            return fallback;
        }
    }

    private GpuTelemetry? ReadNvml()
    {
        if (_nvmlUnavailable)
        {
            return null;
        }
        try
        {
            if (!_initialized)
            {
                if (NvmlInitV2() != 0 || NvmlDeviceGetCountV2(out var count) != 0 ||
                    count == 0 ||
                    NvmlDeviceGetHandleByIndexV2(0, out _device) != 0)
                {
                    _nvmlUnavailable = true;
                    return null;
                }
                _initialized = true;
            }

            if (NvmlDeviceGetUtilizationRates(_device, out var rates) != 0)
            {
                return null;
            }
            var temperature = NvmlDeviceGetTemperature(_device, 0, out var temp) == 0
                ? (int?)temp
                : null;
            var nameBuffer = new byte[96];
            var name = NvmlDeviceGetName(_device, nameBuffer, (uint)nameBuffer.Length) == 0
                ? System.Text.Encoding.UTF8.GetString(nameBuffer).TrimEnd('\0')
                : "NVIDIA GPU";
            _error = null;
            return new GpuTelemetry(rates.Gpu, rates.Memory, temperature, name, "NVML", null);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _nvmlUnavailable = true;
            AppPaths.Log($"NVML unavailable: {ex.Message}");
            return null;
        }
    }

    private GpuTelemetry ReadWindowsCounters()
    {
        try
        {
            if (DateTime.UtcNow - _counterRefresh > TimeSpan.FromSeconds(10) ||
                _fallbackCounters.Length == 0)
            {
                DisposeCounters();
                var category = new PerformanceCounterCategory("GPU Engine");
                _fallbackCounters = category.GetInstanceNames()
                    .Where(name => name.Contains("engtype_3d", StringComparison.OrdinalIgnoreCase))
                    .Select(name => new PerformanceCounter(
                        "GPU Engine", "Utilization Percentage", name, readOnly: true))
                    .ToArray();
                _counterRefresh = DateTime.UtcNow;
            }

            var usage = _fallbackCounters.Sum(counter =>
            {
                try
                {
                    return counter.NextValue();
                }
                catch
                {
                    return 0;
                }
            });
            return new GpuTelemetry(
                Math.Clamp(usage, 0, 100),
                null,
                null,
                "Windows GPU",
                "GPU Engine",
                _error);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return new GpuTelemetry(null, null, null, "NVIDIA GPU", "不可用", ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            DisposeCounters();
            if (_initialized)
            {
                try
                {
                    _ = NvmlShutdown();
                }
                catch
                {
                    // Driver unload during shutdown is harmless.
                }
                _initialized = false;
            }
        }
    }

    private void DisposeCounters()
    {
        foreach (var counter in _fallbackCounters)
        {
            counter.Dispose();
        }
        _fallbackCounters = [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInitV2();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern int NvmlDeviceGetCountV2(out uint count);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlDeviceGetHandleByIndexV2(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int NvmlDeviceGetUtilizationRates(
        IntPtr device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int NvmlDeviceGetTemperature(
        IntPtr device, uint sensorType, out uint temperature);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
    private static extern int NvmlDeviceGetName(
        IntPtr device, byte[] name, uint length);
}

public sealed record GpuTelemetry(
    double? UsagePercent,
    double? MemoryPercent,
    int? TemperatureC,
    string Name,
    string Source,
    string? Error);
