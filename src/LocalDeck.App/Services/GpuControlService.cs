using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace LocalDeck.App.Services;

public sealed class GpuControlService : IDisposable
{
    private readonly object _sync = new();
    private IntPtr _device;
    private bool _initialized;
    private bool _unavailable;
    private GpuControlSnapshot? _cached;
    private DateTime _cachedAt = DateTime.MinValue;
    private string? _error;

    public GpuControlSnapshot Read(bool force = false)
    {
        lock (_sync)
        {
            if (!force && _cached is not null &&
                DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(1))
            {
                return _cached;
            }

            if (_unavailable || !EnsureDevice())
            {
                _cached = Empty();
                _cachedAt = DateTime.UtcNow;
                return _cached;
            }

            try
            {
                var name = ReadName();
                var usage = NvmlDeviceGetUtilizationRates(_device, out var rates) == 0
                    ? (double?)rates.Gpu
                    : null;
                var temperature = NvmlDeviceGetTemperature(_device, 0, out var temp) == 0
                    ? (int?)temp
                    : null;
                var power = NvmlDeviceGetPowerUsage(_device, out var milliwatts) == 0
                    ? (double?)(milliwatts / 1000d)
                    : null;
                var enforced = NvmlDeviceGetEnforcedPowerLimit(_device, out var enforcedMw) == 0
                    ? (double?)(enforcedMw / 1000d)
                    : null;
                var hasConstraints =
                    NvmlDeviceGetPowerManagementLimitConstraints(
                        _device, out var minimumMw, out var maximumMw) == 0 &&
                    maximumMw > minimumMw;
                var defaultLimit =
                    NvmlDeviceGetPowerManagementDefaultLimit(_device, out var defaultMw) == 0
                        ? (double?)(defaultMw / 1000d)
                        : null;
                var graphicsClock =
                    NvmlDeviceGetClockInfo(_device, 0, out var graphicsMhz) == 0
                        ? (int?)graphicsMhz
                        : null;
                var memoryClock =
                    NvmlDeviceGetClockInfo(_device, 2, out var memoryMhz) == 0
                        ? (int?)memoryMhz
                        : null;
                var fanPercent =
                    NvmlDeviceGetFanSpeed_v2(_device, 0, out var fanValue) == 0
                        ? (int?)fanValue
                        : null;
                var fanCount = NvmlDeviceGetNumFans(_device, out var fans) == 0 ? fans : 0;
                var coreOffset =
                    NvmlDeviceGetGpcClkVfOffset(_device, out var core) == 0 ? (int?)core : null;
                var memoryOffset =
                    NvmlDeviceGetMemClkVfOffset(_device, out var memory) == 0 ? (int?)memory : null;
                var minimumLimit = hasConstraints ? minimumMw / 1000d : (double?)null;
                var maximumLimit = hasConstraints ? maximumMw / 1000d : (double?)null;
                if (minimumLimit is not null && defaultLimit is not null)
                {
                    // Mobile drivers can report an impractically low floor. Keep
                    // the exposed range at or above half of the default limit.
                    minimumLimit = Math.Max(
                        minimumLimit.Value, Math.Round(defaultLimit.Value * 0.5));
                }

                _error = null;
                _cached = new GpuControlSnapshot(
                    name,
                    "NVIDIA NVML",
                    true,
                    IsAdministrator(),
                    usage,
                    temperature,
                    power,
                    enforced,
                    defaultLimit,
                    minimumLimit,
                    maximumLimit,
                    hasConstraints && minimumLimit < maximumLimit,
                    fanPercent,
                    fanCount > 0 || fanPercent is not null,
                    coreOffset,
                    memoryOffset,
                    graphicsClock,
                    memoryClock,
                    null);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _cached = Empty();
                AppPaths.Log($"GPU control read failed: {ex.Message}");
            }
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    public async Task<GpuControlResult> ApplyElevatedAsync(string kind, int value)
    {
        if (!TryValidate(kind, value, out var validation))
        {
            return new GpuControlResult(false, validation, Read(force: true));
        }

        var resultPath = Path.Combine(
            Path.GetTempPath(), $"LocalDeck-gpu-{Guid.NewGuid():N}.json");
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath ??
                                             throw new InvalidOperationException("Executable path unavailable."))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--gpu-helper");
            start.ArgumentList.Add(kind);
            start.ArgumentList.Add(value.ToString());
            start.ArgumentList.Add(resultPath);

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("GPU helper could not start.");
            await Task.Run(() => process.WaitForExit(60_000));
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                return new GpuControlResult(false, "GPU 控制助手超时。", Read(force: true));
            }
            if (!File.Exists(resultPath))
            {
                return new GpuControlResult(false, "GPU 控制助手没有返回结果。", Read(force: true));
            }

            var result = JsonSerializer.Deserialize<GpuControlResult>(
                await File.ReadAllTextAsync(resultPath))
                ?? new GpuControlResult(false, "GPU 控制结果无效。", null);
            return result with { Snapshot = Read(force: true) };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new GpuControlResult(false, "已取消管理员授权。", Read(force: true));
        }
        catch (Exception ex)
        {
            AppPaths.Log($"GPU control helper failed: {ex}");
            return new GpuControlResult(false, ex.Message, Read(force: true));
        }
        finally
        {
            try
            {
                File.Delete(resultPath);
            }
            catch
            {
                // Temporary result cleanup is best-effort.
            }
        }
    }

    public static void RunHelper(string[] args)
    {
        var index = Array.FindIndex(
            args, arg => arg.Equals("--gpu-helper", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 3 >= args.Length ||
            !int.TryParse(args[index + 2], out var value))
        {
            return;
        }

        var resultPath = args[index + 3];
        GpuControlResult result;
        try
        {
            using var service = new GpuControlService();
            result = service.ApplyDirect(args[index + 1], value);
        }
        catch (Exception ex)
        {
            result = new GpuControlResult(false, ex.Message, null);
        }

        File.WriteAllText(resultPath, JsonSerializer.Serialize(result));
    }

    public void Dispose()
    {
        lock (_sync)
        {
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

    private GpuControlResult ApplyDirect(string kind, int value)
    {
        if (!TryValidate(kind, value, out var validation))
        {
            return new GpuControlResult(false, validation, Read(force: true));
        }
        if (!EnsureDevice())
        {
            return new GpuControlResult(false, _error ?? "NVML 不可用。", Read(force: true));
        }

        var result = kind switch
        {
            "power" => NvmlDeviceSetPowerManagementLimit(_device, checked((uint)value * 1000)),
            "fan" => NvmlDeviceSetFanSpeed_v2(_device, 0, (uint)value),
            "core-offset" => NvmlDeviceSetGpcClkVfOffset(_device, value),
            "memory-offset" => NvmlDeviceSetMemClkVfOffset(_device, value),
            _ => 2
        };
        var message = result switch
        {
            0 => "设置已应用。",
            3 => "当前显卡或驱动不支持该控制项。",
            4 => "NVIDIA 驱动拒绝写入，请确认没有安全软件或厂商驱动锁定。",
            _ => $"NVML 返回错误代码 {result}。"
        };
        return new GpuControlResult(result == 0, message, Read(force: true));
    }

    private bool TryValidate(string kind, int value, out string error)
    {
        error = "";
        if (kind == "power")
        {
            var snapshot = Read();
            if (snapshot.PowerMinimumWatts is null || snapshot.PowerMaximumWatts is null)
            {
                error = "当前驱动没有公开功耗上限范围。";
                return false;
            }
            if (value < snapshot.PowerMinimumWatts || value > snapshot.PowerMaximumWatts)
            {
                error = $"功耗上限必须位于 {snapshot.PowerMinimumWatts:0}–{snapshot.PowerMaximumWatts:0} W。";
                return false;
            }
        }
        else if (kind == "fan")
        {
            if (value is < 30 or > 100)
            {
                error = "为避免停转风险，风扇固定转速仅允许 30%–100%。";
                return false;
            }
        }
        else if (kind == "core-offset")
        {
            if (value is < -250 or > 250)
            {
                error = "核心频率偏移仅允许 -250–250 MHz。";
                return false;
            }
        }
        else if (kind == "memory-offset")
        {
            if (value is < -1000 or > 1000)
            {
                error = "显存频率偏移仅允许 -1000–1000 MHz。";
                return false;
            }
        }
        else
        {
            error = "未知 GPU 控制项。";
            return false;
        }
        return true;
    }

    private bool EnsureDevice()
    {
        if (_initialized)
        {
            return true;
        }
        if (_unavailable)
        {
            return false;
        }
        try
        {
            if (NvmlInitV2() != 0 ||
                NvmlDeviceGetCountV2(out var count) != 0 ||
                count == 0 ||
                NvmlDeviceGetHandleByIndexV2(0, out _device) != 0)
            {
                _unavailable = true;
                _error = "未找到可通过 NVML 控制的 NVIDIA GPU。";
                return false;
            }
            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            _unavailable = true;
            _error = ex.Message;
            return false;
        }
    }

    private string ReadName()
    {
        var buffer = new byte[96];
        return NvmlDeviceGetName(_device, buffer, (uint)buffer.Length) == 0
            ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0')
            : "NVIDIA GPU";
    }

    private GpuControlSnapshot Empty() =>
        new("NVIDIA GPU", "不可用", false, IsAdministrator(), null, null, null, null, null,
            null, null, false, null, false, null, null, null, null, _error);

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
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

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
    private static extern int NvmlDeviceGetName(IntPtr device, byte[] name, uint length);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int NvmlDeviceGetUtilizationRates(
        IntPtr device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int NvmlDeviceGetTemperature(
        IntPtr device, uint sensorType, out uint temperature);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int NvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    private static extern int NvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")]
    private static extern int NvmlDeviceGetPowerManagementLimitConstraints(
        IntPtr device, out uint minimumMilliwatts, out uint maximumMilliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")]
    private static extern int NvmlDeviceGetPowerManagementDefaultLimit(
        IntPtr device, out uint milliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetPowerManagementLimit")]
    private static extern int NvmlDeviceSetPowerManagementLimit(
        IntPtr device, uint milliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed_v2")]
    private static extern int NvmlDeviceGetFanSpeed_v2(
        IntPtr device, uint fan, out uint percent);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetNumFans")]
    private static extern int NvmlDeviceGetNumFans(IntPtr device, out uint count);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetFanSpeed_v2")]
    private static extern int NvmlDeviceSetFanSpeed_v2(
        IntPtr device, uint fan, uint percent);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
    private static extern int NvmlDeviceGetClockInfo(
        IntPtr device, uint clockType, out uint megahertz);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetGpcClkVfOffset")]
    private static extern int NvmlDeviceGetGpcClkVfOffset(IntPtr device, out int offset);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetGpcClkVfOffset")]
    private static extern int NvmlDeviceSetGpcClkVfOffset(IntPtr device, int offset);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemClkVfOffset")]
    private static extern int NvmlDeviceGetMemClkVfOffset(IntPtr device, out int offset);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetMemClkVfOffset")]
    private static extern int NvmlDeviceSetMemClkVfOffset(IntPtr device, int offset);
}

public sealed record GpuControlSnapshot(
    string Name,
    string Source,
    bool Available,
    bool IsAdministrator,
    double? UsagePercent,
    int? TemperatureC,
    double? PowerUsageWatts,
    double? PowerLimitWatts,
    double? PowerDefaultWatts,
    double? PowerMinimumWatts,
    double? PowerMaximumWatts,
    bool PowerWritable,
    int? FanPercent,
    bool FanWritable,
    int? CoreOffsetMhz,
    int? MemoryOffsetMhz,
    int? GraphicsClockMhz,
    int? MemoryClockMhz,
    string? Error = null);

public sealed record GpuControlResult(
    bool Ok,
    string Message,
    GpuControlSnapshot? Snapshot);
