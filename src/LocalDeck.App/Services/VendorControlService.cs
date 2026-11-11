using System.Runtime.InteropServices;

namespace LocalDeck.App.Services;

public sealed class VendorControlService
{
    private static readonly Guid ClassId = new("679A116F-2811-4216-8FD8-A40A1A2C6AD9");
    private static readonly Guid InterfaceId = new("890D3AF2-2860-4498-99A0-D802C6757BF3");
    private readonly object _sync = new();
    private VendorTelemetry? _cached;
    private DateTime _cachedAt = DateTime.MinValue;

    public bool IsAvailable { get; private set; } = true;

    public VendorTelemetry ReadTelemetry()
    {
        lock (_sync)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(2))
            {
                return _cached;
            }

            try
            {
                var client = CreateClient();
                var cpuFan = ReadFan(client, 0);
                var gpuFan = ReadFan(client, 1);
                var cpuTemperature = ReadSystemTemperature(client, 1);
                var gpuTemperature = ReadThermalTemperature(client, 0);
                IsAvailable = true;
                _cached = new VendorTelemetry(
                    cpuFan, gpuFan, cpuTemperature, gpuTemperature, "Pegatron GFN");
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                _cached = new VendorTelemetry(null, null, null, null, null, ex.Message);
                AppPaths.Log($"Vendor telemetry unavailable: {ex.Message}");
            }

            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    public bool SetPerformanceMode(string mode)
    {
        var numericMode = mode.ToLowerInvariant() switch
        {
            "office" or "quiet" or "balance" => 0,
            "gaming" or "performance" => 1,
            "turbo" or "overclock" => 2,
            _ => -1
        };

        if (numericMode < 0)
        {
            return false;
        }

        try
        {
            var client = CreateClient();
            SetFanMode(client, (byte)numericMode);

            var hotkeyWindow = FindWindow("PHotkey", "PHotkey");
            if (hotkeyWindow != IntPtr.Zero)
            {
                SendMessage(hotkeyWindow, 0x9001, (IntPtr)252, (IntPtr)numericMode);
            }

            _cached = null;
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Performance mode switch failed: {ex.Message}");
            return false;
        }
    }

    private static IImpAgfn CreateClient()
    {
        var type = Type.GetTypeFromCLSID(ClassId, throwOnError: true)
            ?? throw new InvalidOperationException("Pegatron GFN COM class is not registered.");
        var instance = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Pegatron GFN COM object could not be created.");
        var client = (IImpAgfn)instance;
        byte data = 0;
        var result = 0;
        client.Identify(ref data, ref result);
        return client;
    }

    private static int? ReadFan(IImpAgfn client, byte fan)
    {
        var packet = new Function63
        {
            Header = new Header
            {
                MainFunction = 99,
                SubFunction = 3,
                BufferLength = (short)Marshal.SizeOf<Function63>()
            },
            Fan = new FanInformation { Fan = fan }
        };

        var response = Invoke(client, packet);
        var tach = response.Fan.Tach;
        if (tach == 0 || tach == ushort.MaxValue)
        {
            return null;
        }

        return (int)Math.Round(2_156_250d / tach);
    }

    private static int? ReadSystemTemperature(IImpAgfn client, byte device)
    {
        var packet = new Function63
        {
            Header = new Header
            {
                MainFunction = 99,
                SubFunction = 2,
                BufferLength = (short)Marshal.SizeOf<Function63>()
            },
            SystemTemperature = new SystemTemperature { Device = device }
        };

        var response = Invoke(client, packet);
        return response.SystemTemperature.Temperature is > 0 and < 125
            ? response.SystemTemperature.Temperature
            : null;
    }

    private static int? ReadThermalTemperature(IImpAgfn client, byte sensor)
    {
        var packet = new Function63
        {
            Header = new Header
            {
                MainFunction = 99,
                SubFunction = 1,
                BufferLength = (short)Marshal.SizeOf<Function63>()
            },
            ThermalTemperature = new ThermalTemperature { Sensor = sensor }
        };

        var response = Invoke(client, packet);
        return response.ThermalTemperature.Left is > 0 and < 125
            ? response.ThermalTemperature.Left
            : null;
    }

    private static void SetFanMode(IImpAgfn client, byte mode)
    {
        var packet = new Function68
        {
            Header = new Header
            {
                MainFunction = 104,
                SubFunction = 3,
                BufferLength = (short)Marshal.SizeOf<Function68>()
            },
            Mode = mode
        };

        _ = Invoke(client, packet);
    }

    private static T Invoke<T>(IImpAgfn client, T packet) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(packet, memory, false);
            var result = 0;
            client.GenericFunction(memory, ref result);
            if (result != 0)
            {
                throw new InvalidOperationException($"GFN call failed with result {result}.");
            }

            return Marshal.PtrToStructure<T>(memory);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [ComImport]
    [Guid("890D3AF2-2860-4498-99A0-D802C6757BF3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImpAgfn
    {
        int Identify(ref byte data, ref int result);
        int GenericFunction(IntPtr structure, ref int result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public short MainFunction;
        public short SubFunction;
        public short BufferLength;
        public byte Status;
        public byte ErrorCode;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Function63
    {
        [FieldOffset(0)]
        public Header Header;

        [FieldOffset(8)]
        public ThermalTemperature ThermalTemperature;

        [FieldOffset(8)]
        public SystemTemperature SystemTemperature;

        [FieldOffset(8)]
        public FanInformation Fan;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThermalTemperature
    {
        public byte Sensor;
        public byte Right;
        public byte Left;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTemperature
    {
        public byte Device;
        public byte Temperature;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FanInformation
    {
        public byte Fan;
        public byte Status;
        public ushort Tach;
        public byte Trigger;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Function68
    {
        [FieldOffset(0)]
        public Header Header;

        [FieldOffset(8)]
        public byte Mode;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

public sealed record VendorTelemetry(
    int? CpuFanRpm,
    int? GpuFanRpm,
    int? CpuTemperature,
    int? GpuTemperature,
    string? Source,
    string? Error = null);
