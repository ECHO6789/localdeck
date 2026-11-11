using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LocalDeck.App.Models;

namespace LocalDeck.App.Services;

public sealed class SystemMonitorService
{
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private long _lastReceived;
    private long _lastSent;
    private DateTime _lastNetworkSample = DateTime.UtcNow;
    private readonly object _sync = new();

    public double ReadCpuPercent()
    {
        lock (_sync)
        {
            return ReadCpu();
        }
    }

    public SystemLiteSnapshot ReadLite()
    {
        lock (_sync)
        {
            var cpu = ReadCpu();
            var memory = new MemoryStatus();
            memory.Length = (uint)Marshal.SizeOf<MemoryStatus>();
            _ = GlobalMemoryStatusEx(ref memory);
            var (down, up) = ReadNetwork();
            var battery = ReadBattery();
            var disks = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
                .Select(drive =>
                {
                    try
                    {
                        return new DiskUsageSnapshot(
                            drive.Name, drive.TotalSize, drive.AvailableFreeSpace);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(item => item is not null)
                .Cast<DiskUsageSnapshot>()
                .ToList();
            var total = (ulong)memory.TotalPhysical;
            var available = (ulong)memory.AvailablePhysical;
            return new SystemLiteSnapshot(
                cpu,
                total - available,
                total,
                battery.Percent,
                battery.Charging,
                down,
                up,
                TimeSpan.FromMilliseconds(Environment.TickCount64),
                disks);
        }
    }

    public SystemSnapshot Read()
    {
        lock (_sync)
        {
            var cpu = ReadCpu();
            var memory = new MemoryStatus();
            memory.Length = (uint)Marshal.SizeOf<MemoryStatus>();
            GlobalMemoryStatusEx(ref memory);

            var (down, up) = ReadNetwork();
            var battery = ReadBattery();
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

            var disks = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(drive =>
                {
                    try
                    {
                        return new DiskSnapshot(
                            drive.Name,
                            string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.DriveFormat : drive.VolumeLabel,
                            drive.TotalSize,
                            drive.AvailableFreeSpace);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(item => item is not null)
                .Cast<DiskSnapshot>()
                .ToList();

            var processes = Process.GetProcesses()
                .Select(process =>
                {
                    try
                    {
                        return new ProcessSnapshot(
                            process.Id,
                            process.ProcessName,
                            process.WorkingSet64,
                            process.TotalProcessorTime.TotalSeconds);
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
                .Where(item => item is not null)
                .Cast<ProcessSnapshot>()
                .OrderByDescending(item => item.WorkingSetBytes)
                .Take(6)
                .ToList();

            var total = (ulong)memory.TotalPhysical;
            var available = (ulong)memory.AvailablePhysical;
            return new SystemSnapshot(
                cpu,
                total - available,
                total,
                battery.Percent,
                battery.Charging,
                $"{(int)uptime.TotalDays}天 {uptime.Hours:D2}:{uptime.Minutes:D2}",
                down,
                up,
                disks,
                processes);
        }
    }

    private double ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var idleValue = ToUInt64(idle);
        var kernelValue = ToUInt64(kernel);
        var userValue = ToUInt64(user);
        var idleDelta = idleValue - _lastIdle;
        var kernelDelta = kernelValue - _lastKernel;
        var userDelta = userValue - _lastUser;
        _lastIdle = idleValue;
        _lastKernel = kernelValue;
        _lastUser = userValue;

        var total = kernelDelta + userDelta;
        return total == 0 ? 0 : Math.Clamp((total - idleDelta) * 100d / total, 0, 100);
    }

    private (double Down, double Up) ReadNetwork()
    {
        long received = 0;
        long sent = 0;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            try
            {
                var statistics = adapter.GetIPStatistics();
                received += statistics.BytesReceived;
                sent += statistics.BytesSent;
            }
            catch
            {
                // Some virtual adapters do not expose statistics.
            }
        }

        var now = DateTime.UtcNow;
        var seconds = Math.Max(0.2, (now - _lastNetworkSample).TotalSeconds);
        var down = _lastReceived == 0 ? 0 : Math.Max(0, received - _lastReceived) / seconds;
        var up = _lastSent == 0 ? 0 : Math.Max(0, sent - _lastSent) / seconds;
        _lastReceived = received;
        _lastSent = sent;
        _lastNetworkSample = now;
        return (down, up);
    }

    private static (int? Percent, bool Charging) ReadBattery()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryLifePercent == byte.MaxValue)
        {
            return (null, false);
        }

        return (status.BatteryLifePercent, status.BatteryFlag is not 128 and not 255 &&
            status.ACLineStatus == 1);
    }

    private static ulong ToUInt64(FileTime value) =>
        ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}

public sealed record SystemLiteSnapshot(
    double CpuPercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    int? BatteryPercent,
    bool BatteryCharging,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    TimeSpan Uptime,
    IReadOnlyList<DiskUsageSnapshot> Disks);

public sealed record DiskUsageSnapshot(
    string Name,
    long TotalBytes,
    long FreeBytes);
