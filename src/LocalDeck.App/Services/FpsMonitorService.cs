using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace LocalDeck.App.Services;

public sealed class FpsMonitorService : IDisposable
{
    private static readonly Guid DxgiProvider =
        new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider =
        new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private readonly ConcurrentDictionary<int, long> _totals = new();
    private readonly Dictionary<int, long> _previous = [];
    private readonly object _sync = new();
    private TraceEventSession? _session;
    private Task? _processingTask;
    private Process? _helperProcess;
    private bool _helperAvailable;
    private bool _etwAttempted;
    private Stopwatch _clock = Stopwatch.StartNew();
    private string? _error;

    public bool IsRunning => _processingTask is { IsCompleted: false };
    public string? Error
    {
        get
        {
            lock (_sync)
            {
                return _error;
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_processingTask is { IsCompleted: false })
            {
                return;
            }
            if (_etwAttempted)
            {
                return;
            }
            _etwAttempted = true;
            _error = null;
            try
            {
                _session = new TraceEventSession("LocalDeck-FPS")
                {
                    StopOnDispose = true
                };
                _session.EnableProvider(
                    DxgiProvider,
                    TraceEventLevel.Verbose,
                    0x8000000000000002);
                _session.EnableProvider(
                    D3D9Provider,
                    TraceEventLevel.Verbose,
                    0x8000000000000002);
                _session.Source.AllEvents += OnEvent;
                _processingTask = Task.Run(() =>
                {
                    try
                    {
                        _session.Source.Process();
                    }
                    catch (Exception ex)
                    {
                        lock (_sync)
                        {
                            _error = ex.Message;
                        }
                        AppPaths.Log($"FPS ETW session stopped: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _session?.Dispose();
                _session = null;
                AppPaths.Log($"FPS ETW session unavailable: {ex.Message}");
            }
        }
    }

    public bool LaunchElevatedHelper()
    {
        lock (_sync)
        {
            if (_helperProcess is { HasExited: false })
            {
                return true;
            }
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                _error = "无法确定 LocalDeck 可执行文件路径。";
                return false;
            }
            try
            {
                _helperProcess = Process.Start(new ProcessStartInfo(
                    executable,
                    $"--fps-helper {Environment.ProcessId}")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                _error = _helperProcess is null ? "FPS Helper 启动失败。" : null;
                return _helperProcess is not null;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                AppPaths.Log($"FPS helper launch failed: {ex.Message}");
                return false;
            }
        }
    }

    public FpsSample Sample(string source = "foreground")
    {
        Start();
        ReadHelperSnapshot();
        var foreground = GetForegroundProcess();
        var elapsed = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        if (elapsed <= 0)
        {
            elapsed = 1;
        }

        var current = _totals.ToArray();
        var deltas = new Dictionary<int, double>();
        foreach (var (pid, total) in current)
        {
            _previous.TryGetValue(pid, out var previous);
            deltas[pid] = Math.Max(0, total - previous) / elapsed;
        }
        _previous.Clear();
        foreach (var (pid, count) in current)
        {
            _previous[pid] = count;
        }

        if (source.StartsWith("screen:", StringComparison.OrdinalIgnoreCase))
        {
            var displayName = source[7..];
            return new FpsSample(
                null,
                $"屏幕 {displayName}",
                true,
                null,
                RefreshRate(displayName));
        }

        ProcessIdentity? selected;
        if (source.StartsWith("process:", StringComparison.OrdinalIgnoreCase))
        {
            var processName = source[8..];
            selected = current
                .Select(item => GetProcessInfo(item.Key))
                .Where(item => item is not null &&
                               item.Name.Equals(processName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => deltas.GetValueOrDefault(item!.Id))
                .FirstOrDefault();
        }
        else
        {
            selected = foreground is not null &&
                       deltas.GetValueOrDefault(foreground.Id) > 0
                ? foreground
                : current
                    .Where(item => item.Key != Environment.ProcessId)
                    .OrderByDescending(item => deltas.GetValueOrDefault(item.Key))
                    .Select(item => GetProcessInfo(item.Key))
                    .FirstOrDefault(item => item is not null);
        }

        var fps = selected is null ? 0 : deltas.GetValueOrDefault(selected.Id);
        return new FpsSample(
            fps > 0.5 ? Math.Round(fps, 1) : null,
            selected?.Name,
            IsRunning || _helperAvailable,
            Error,
            RefreshRate());
    }

    public void Dispose()
    {
        TraceEventSession? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
        }
        session?.Dispose();
        try
        {
            if (_helperProcess is { HasExited: false })
            {
                _helperProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The elevated helper also stops when its parent process disappears.
        }
        _helperProcess?.Dispose();
        try
        {
            _processingTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ETW source processing exits when its session is disposed.
        }
    }

    private void OnEvent(TraceEvent data)
    {
        if ((data.ProviderGuid == DxgiProvider && (int)data.ID == 0x2A) ||
            (data.ProviderGuid == D3D9Provider && (int)data.ID == 0x01))
        {
            _totals.AddOrUpdate(data.ProcessID, 1, (_, value) => value + 1);
        }
    }

    private void ReadHelperSnapshot()
    {
        if (IsRunning)
        {
            return;
        }
        try
        {
            var path = FpsHelperSnapshotPath;
            if (!File.Exists(path))
            {
                _helperAvailable = false;
                return;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var updatedAt = root.TryGetProperty("updatedAt", out var updated)
                ? updated.GetDateTimeOffset()
                : DateTimeOffset.MinValue;
            if (DateTimeOffset.UtcNow - updatedAt > TimeSpan.FromSeconds(4))
            {
                _helperAvailable = false;
                return;
            }

            var values = new Dictionary<int, long>();
            if (root.TryGetProperty("processes", out var processes) &&
                processes.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in processes.EnumerateObject())
                {
                    if (int.TryParse(property.Name, out var pid) &&
                        property.Value.TryGetInt64(out var count))
                    {
                        values[pid] = count;
                    }
                }
            }

            _totals.Clear();
            foreach (var (pid, count) in values)
            {
                _totals[pid] = count;
            }
            _helperAvailable = true;
            lock (_sync)
            {
                _error = null;
            }
        }
        catch
        {
            _helperAvailable = false;
        }
    }

    public static void RunHelper(int parentProcessId)
    {
        using var single = new Mutex(true, @"Local\LocalDeck.FpsHelper", out var created);
        if (!created)
        {
            return;
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);
        var counts = new ConcurrentDictionary<int, long>();
        using var cancellation = new CancellationTokenSource();
        using var session = new TraceEventSession("LocalDeck-FPS-Helper")
        {
            StopOnDispose = true
        };
        try
        {
            session.EnableProvider(
                DxgiProvider,
                TraceEventLevel.Verbose,
                0x8000000000000002);
            session.EnableProvider(
                D3D9Provider,
                TraceEventLevel.Verbose,
                0x8000000000000002);
            session.Source.AllEvents += data =>
            {
                if ((data.ProviderGuid == DxgiProvider && (int)data.ID == 0x2A) ||
                    (data.ProviderGuid == D3D9Provider && (int)data.ID == 0x01))
                {
                    counts.AddOrUpdate(data.ProcessID, 1, (_, value) => value + 1);
                }
            };
            var processing = Task.Run(() => session.Source.Process(), cancellation.Token);
            while (!cancellation.IsCancellationRequested)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    updatedAt = DateTimeOffset.UtcNow,
                    processes = counts.ToDictionary(item => item.Key, item => item.Value)
                });
                var temporary = FpsHelperSnapshotPath + ".tmp";
                File.WriteAllText(temporary, payload);
                File.Move(temporary, FpsHelperSnapshotPath, overwrite: true);
                Thread.Sleep(500);

                if (parentProcessId > 0)
                {
                    try
                    {
                        using var parent = Process.GetProcessById(parentProcessId);
                        if (parent.HasExited)
                        {
                            break;
                        }
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            cancellation.Cancel();
            session.Dispose();
            try
            {
                processing.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Session disposal stops the ETW pump.
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"FPS helper failed: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(FpsHelperSnapshotPath);
            }
            catch
            {
                // A stale snapshot expires by timestamp.
            }
        }
    }

    private static string FpsHelperSnapshotPath =>
        Path.Combine(AppPaths.DataDirectory, "fps-helper.json");

    private static ProcessIdentity? GetForegroundProcess()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return null;
        }
        _ = GetWindowThreadProcessId(window, out var processId);
        return GetProcessInfo((int)processId);
    }

    private static ProcessIdentity? GetProcessInfo(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new ProcessIdentity(processId, process.ProcessName);
        }
        catch
        {
            return null;
        }
    }

    private static int? RefreshRate(string? deviceName = null)
    {
        try
        {
            var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            return EnumDisplaySettings(deviceName, -1, ref mode) && mode.DisplayFrequency > 0
                ? mode.DisplayFrequency
                : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TrueType;
        public short Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    private sealed record ProcessIdentity(int Id, string Name);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(
        string? deviceName, int modeNumber, ref DevMode devMode);
}

public sealed record FpsSample(
    double? Fps,
    string? ProcessName,
    bool EtwAvailable,
    string? Error,
    int? RefreshHz);
