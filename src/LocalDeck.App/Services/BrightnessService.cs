using System.Management;
using System.Runtime.InteropServices;

namespace LocalDeck.App.Services;

public sealed class BrightnessService
{
    private readonly object _sync = new();
    private readonly GammaBrightnessFallback _gamma = new();

    public IReadOnlyList<DisplayBrightness> GetDisplays()
    {
        var wmiMonitors = ReadWmiMonitors();
        var wmiBrightness = ReadWmiBrightness();
        var gdiTargets = ReadGdiTargets();
        var result = new List<DisplayBrightness>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in gdiTargets.Values)
        {
            var key = NormalizePnpId(target.DeviceId);
            if (!seen.Add(key))
            {
                continue;
            }

            wmiMonitors.TryGetValue(key, out var wmi);
            wmiBrightness.TryGetValue(key, out var wmiLevel);
            var name = FirstNonEmpty(
                wmi?.FriendlyName,
                wmi?.Manufacturer is "BOE" or "AUO" or "EDO" or "SDC" or "LGD"
                    ? "内置显示器"
                    : null,
                FriendlyTargetName(target.DeviceString),
                target.DeviceName,
                "显示器");

            DisplayBrightness item;
            if (wmiLevel is not null && wmiLevel.CanSet)
            {
                item = new DisplayBrightness(
                    $"wmi:{EscapeId(key)}",
                    name,
                    wmiLevel.Current,
                    0,
                    100,
                    "WMI",
                    true,
                    true,
                    true,
                    key,
                    target.DeviceName,
                    null);
            }
            else if (TryReadDdcBrightness(target.DeviceName, out var ddc))
            {
                item = new DisplayBrightness(
                    $"ddc:{EscapeId(target.DeviceName)}:{ddc.VcpCode}",
                    name,
                    ddc.Current,
                    ddc.Minimum,
                    ddc.Maximum,
                    "DDC/CI",
                    true,
                    true,
                    true,
                    key,
                    target.DeviceName,
                    null);
            }
            else
            {
                var gammaCurrent = _gamma.GetBrightness(target.DeviceName);
                item = new DisplayBrightness(
                    $"gamma:{EscapeId(target.DeviceName)}",
                    name,
                    gammaCurrent,
                    20,
                    100,
                    "Gamma（软件）",
                    true,
                    false,
                    true,
                    key,
                    target.DeviceName,
                    "显示器已识别；硬件亮度接口不可用，正在使用软件调光。");
            }

            result.Add(item);
        }

        foreach (var (key, monitor) in wmiMonitors)
        {
            if (!seen.Add(key))
            {
                continue;
            }

            wmiBrightness.TryGetValue(key, out var wmiLevel);
            var canSet = wmiLevel is { CanSet: true };
            result.Add(new DisplayBrightness(
                canSet ? $"wmi:{EscapeId(key)}" : $"detected:{EscapeId(key)}",
                FirstNonEmpty(
                    monitor.FriendlyName,
                    monitor.Manufacturer is "BOE" or "AUO" or "EDO" or "SDC" or "LGD"
                        ? "内置显示器"
                        : null,
                    "已检测显示器"),
                canSet ? wmiLevel!.Current : 100,
                canSet ? 0 : 20,
                100,
                canSet ? "WMI" : "仅检测",
                monitor.Active,
                canSet,
                canSet,
                key,
                null,
                canSet ? null : "显示器已通过 PnP/EDID 识别，但没有可用的亮度通道。"));
        }

        return result
            .OrderByDescending(item => item.IsActive)
            .ThenByDescending(item => item.SupportsHardwareControl)
            .ThenBy(item => item.Name)
            .ToList();
    }

    public bool SetBrightness(string id, int value)
    {
        value = Math.Clamp(value, 0, 100);
        if (id.StartsWith("wmi:", StringComparison.Ordinal))
        {
            return SetWmiBrightness(id[4..], value);
        }
        if (id.StartsWith("ddc:", StringComparison.Ordinal))
        {
            var parts = id[4..].Split(':', 2);
            return SetDdcBrightness(
                parts[0],
                value,
                parts.Length == 2 && byte.TryParse(parts[1], out var code)
                    ? code
                    : (byte)0);
        }
        if (id.StartsWith("gamma:", StringComparison.Ordinal))
        {
            return _gamma.SetBrightness(UnescapeId(id[6..]), value);
        }
        return false;
    }

    private static Dictionary<string, WmiMonitorRecord> ReadWmiMonitors()
    {
        var result = new Dictionary<string, WmiMonitorRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM WmiMonitorID");
            using var collection = searcher.Get();
            foreach (ManagementObject item in collection)
            {
                var instance = item["InstanceName"]?.ToString();
                if (string.IsNullOrWhiteSpace(instance))
                {
                    continue;
                }
                var key = NormalizePnpId(instance);
                var active = false;
                try
                {
                    active = Convert.ToBoolean(item["Active"] ?? false);
                }
                catch
                {
                    active = true;
                }
                result[key] = new WmiMonitorRecord(
                    key,
                    instance,
                    DecodeWmiString(item["UserFriendlyName"]),
                    DecodeWmiString(item["ManufacturerName"]),
                    DecodeWmiString(item["ProductCodeID"]),
                    DecodeWmiString(item["SerialNumberID"]),
                    active);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"WMI monitor identity query failed: {ex.Message}");
        }
        return result;
    }

    private static Dictionary<string, WmiBrightnessRecord> ReadWmiBrightness()
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var methodKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var brightnessSearcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT InstanceName,CurrentBrightness FROM WmiMonitorBrightness");
            using var brightness = brightnessSearcher.Get();
            foreach (ManagementObject item in brightness)
            {
                var instance = item["InstanceName"]?.ToString();
                if (!string.IsNullOrWhiteSpace(instance))
                {
                    values[NormalizePnpId(instance)] = Convert.ToInt32(item["CurrentBrightness"]);
                }
            }

            using var methodSearcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT InstanceName FROM WmiMonitorBrightnessMethods");
            using var methods = methodSearcher.Get();
            foreach (ManagementObject item in methods)
            {
                var instance = item["InstanceName"]?.ToString();
                if (!string.IsNullOrWhiteSpace(instance))
                {
                    methodKeys.Add(NormalizePnpId(instance));
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"WMI monitor brightness query failed: {ex.Message}");
        }

        return values.ToDictionary(
            pair => pair.Key,
            pair => new WmiBrightnessRecord(pair.Value, methodKeys.Contains(pair.Key)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool SetWmiBrightness(string encodedKey, int value)
    {
        var key = UnescapeId(encodedKey);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT InstanceName FROM WmiMonitorBrightnessMethods");
            using var methods = searcher.Get();
            foreach (ManagementObject method in methods)
            {
                var instance = method["InstanceName"]?.ToString();
                if (string.IsNullOrWhiteSpace(instance) ||
                    !NormalizePnpId(instance).Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                method.InvokeMethod("WmiSetBrightness", [0u, (byte)value]);
                return true;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"WMI brightness set failed: {ex.Message}");
        }
        return false;
    }

    private static bool SetDdcBrightness(string encodedDisplayName, int value)
    {
        var displayName = UnescapeId(encodedDisplayName);
        foreach (var monitor in EnumeratePhysicalMonitors())
        {
            try
            {
                if (!monitor.DeviceName.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                uint minimum;
                uint maximum;
                if (!GetMonitorBrightness(
                        monitor.Handle, out minimum, out _, out maximum))
                {
                    if (!GetVcpFeatureAndVcpFeatureReply(
                            monitor.Handle, 0x10, out _, out _, out maximum))
                    {
                        return false;
                    }
                    minimum = 0;
                }
                var raw = minimum + (maximum - minimum) * (uint)value / 100u;
                return SetMonitorBrightness(monitor.Handle, raw);
            }
            finally
            {
                DestroyPhysicalMonitor(monitor.Handle);
            }
        }
        return false;
    }

    private static bool SetDdcBrightness(
        string encodedDisplayName, int value, byte vcpCode)
    {
        if (vcpCode == 0)
        {
            return SetDdcBrightness(encodedDisplayName, value);
        }

        var displayName = UnescapeId(encodedDisplayName);
        foreach (var monitor in EnumeratePhysicalMonitors())
        {
            try
            {
                if (!monitor.DeviceName.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
                    !GetVcpFeatureAndVcpFeatureReply(
                        monitor.Handle, vcpCode, out _, out _, out var maximum) ||
                    maximum == 0)
                {
                    continue;
                }
                var raw = (uint)Math.Round(maximum * value / 100d);
                return SetVcpFeature(monitor.Handle, vcpCode, raw);
            }
            finally
            {
                DestroyPhysicalMonitor(monitor.Handle);
            }
        }
        return false;
    }

    private static bool TryReadDdcBrightness(
        string displayName, out DdcBrightness brightness)
    {
        foreach (var monitor in EnumeratePhysicalMonitors())
        {
            try
            {
                if (!monitor.DeviceName.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
                    !TryReadMonitorBrightness(monitor.Handle, out brightness))
                {
                    continue;
                }
                return true;
            }
            finally
            {
                DestroyPhysicalMonitor(monitor.Handle);
            }
        }
        brightness = default;
        return false;
    }

    private static bool TryReadMonitorBrightness(
        IntPtr handle, out DdcBrightness brightness)
    {
        if (GetMonitorBrightness(handle, out var minimum, out var current, out var maximum) &&
            maximum > minimum)
        {
            brightness = new DdcBrightness(
                (int)minimum, (int)current, (int)maximum, 0);
            return true;
        }

        foreach (var code in new byte[] { 0x10, 0x6B, 0x13 })
        {
            if (GetVcpFeatureAndVcpFeatureReply(
                    handle, code, out _, out current, out maximum) &&
                maximum > 0)
            {
                brightness = new DdcBrightness(0, (int)current, (int)maximum, code);
                return true;
            }
        }

        brightness = default;
        return false;
    }

    private static Dictionary<string, GdiTarget> ReadGdiTargets()
    {
        var result = new Dictionary<string, GdiTarget>(StringComparer.OrdinalIgnoreCase);
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapter = DisplayDevice.Create();
            if (!EnumDisplayDevices(null, adapterIndex, ref adapter, 0))
            {
                break;
            }
            if ((adapter.StateFlags & DisplayDevice.AttachedToDesktop) == 0)
            {
                continue;
            }

            for (uint targetIndex = 0; ; targetIndex++)
            {
                var target = DisplayDevice.Create();
                if (!EnumDisplayDevices(
                        adapter.DeviceName, targetIndex, ref target,
                        DisplayDevice.EddGetDeviceInterfaceName))
                {
                    break;
                }
                if ((target.StateFlags & DisplayDevice.AttachedToDesktop) == 0 ||
                    string.IsNullOrWhiteSpace(target.DeviceID))
                {
                    continue;
                }
                result[adapter.DeviceName] = new GdiTarget(
                    adapter.DeviceName,
                    target.DeviceID,
                    FirstNonEmpty(target.DeviceString, adapter.DeviceString));
            }
        }
        return result;
    }

    private static List<PhysicalMonitorEntry> EnumeratePhysicalMonitors()
    {
        var result = new List<PhysicalMonitorEntry>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = MonitorInfoEx.Create();
            if (!GetMonitorInfo(monitor, ref info) ||
                !GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) ||
                count == 0)
            {
                return true;
            }

            var handles = new PhysicalMonitor[count];
            if (GetPhysicalMonitorsFromHMONITOR(monitor, count, handles))
            {
                result.AddRange(handles
                    .Where(item => item.Handle != IntPtr.Zero)
                    .Select(item => new PhysicalMonitorEntry(info.DeviceName, item)));
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string DecodeWmiString(object? value)
    {
        try
        {
            if (value is not System.Collections.IEnumerable sequence || value is string)
            {
                return "";
            }
            var characters = new List<char>();
            foreach (var item in sequence)
            {
                var code = Convert.ToInt32(item);
                if (code > 0)
                {
                    characters.Add((char)code);
                }
            }
            return new string(characters.ToArray()).Trim();
        }
        catch
        {
            return "";
        }
    }

    internal static string NormalizePnpId(string value)
    {
        var normalized = value.Replace("\\\\?\\", "", StringComparison.Ordinal).Trim();
        var interfaceMarker = normalized.IndexOf("#{", StringComparison.Ordinal);
        if (interfaceMarker > 0)
        {
            normalized = normalized[..interfaceMarker];
        }
        normalized = normalized.Replace('#', '\\').TrimEnd('_');
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized, @"_\d+$", "");
        return normalized;
    }

    private static string EscapeId(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string UnescapeId(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static string FriendlyTargetName(string value)
    {
        var start = value.IndexOf('(');
        var end = value.LastIndexOf(')');
        if (start >= 0 && end > start)
        {
            return value[(start + 1)..end];
        }
        if (value.Contains("Integrated", StringComparison.OrdinalIgnoreCase))
        {
            return "内置显示器";
        }
        if (value.Contains("PnP", StringComparison.OrdinalIgnoreCase))
        {
            return "PnP 显示器";
        }
        return value.Replace("Generic ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Monitor", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private sealed class GammaBrightnessFallback
    {
        private const int MinimumBrightness = 20;
        private readonly Dictionary<string, GammaState> _states =
            new(StringComparer.OrdinalIgnoreCase);

        public int GetBrightness(string displayName)
        {
            lock (_states)
            {
                var normalized = NormalizeDisplay(displayName);
                if (_states.TryGetValue(normalized, out var state))
                {
                    return state.Brightness;
                }
                using var dc = DisplayDc.Open(normalized);
                if (dc is null || !dc.Value.ReadRamp(out var ramp))
                {
                    return 100;
                }
                _states[normalized] = GammaState.FromRamp(ramp);
                return _states[normalized].Brightness;
            }
        }

        public bool SetBrightness(string displayName, int brightness)
        {
            brightness = Math.Clamp(brightness, MinimumBrightness, 100);
            lock (_states)
            {
                var normalized = NormalizeDisplay(displayName);
                if (!_states.TryGetValue(normalized, out var state))
                {
                    using var initialDc = DisplayDc.Open(normalized);
                    if (initialDc is null || !initialDc.Value.ReadRamp(out var current))
                    {
                        return false;
                    }
                    state = GammaState.FromRamp(current);
                    _states[normalized] = state;
                }

                var ramp = GammaState.BuildRamp(state.BaseRamp, brightness);
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    using var dc = DisplayDc.Open(normalized);
                    if (dc is not null && dc.Value.SetRamp(ramp))
                    {
                        state.Brightness = brightness;
                        if (brightness == 100)
                        {
                            _states.Remove(normalized);
                        }
                        return true;
                    }
                    Thread.Sleep(20 * (attempt + 1));
                }
                return false;
            }
        }

        private static string NormalizeDisplay(string name)
        {
            var suffix = name.IndexOf("\\Monitor", StringComparison.OrdinalIgnoreCase);
            return suffix < 0 ? name : name[..suffix];
        }
    }

    private sealed class GammaState
    {
        public required ushort[] BaseRamp { get; init; }
        public int Brightness { get; set; } = 100;

        public static GammaState FromRamp(ushort[] ramp)
        {
            var top = 0;
            for (var channel = 0; channel < 3; channel++)
            {
                top = Math.Max(top, ramp[channel * 256 + 255]);
            }
            return new GammaState
            {
                BaseRamp = (ushort[])ramp.Clone(),
                Brightness = Math.Clamp((int)Math.Round(top / 65535d * 100), 20, 100)
            };
        }

        public static ushort[] BuildRamp(ushort[] baseRamp, int brightness)
        {
            var curveBrightness = 60d +
                (brightness - 20d) * (100d - 60d) / (100d - 20d);
            var requestedScale = curveBrightness / 100d;
            var peakScale = Math.Max(curveBrightness, 70d) / 100d;
            var exponent = curveBrightness < 70d
                ? Math.Log(requestedScale * 0.5d / peakScale) / Math.Log(0.5d)
                : 1d;
            var result = new ushort[baseRamp.Length];
            for (var i = 0; i < result.Length; i++)
            {
                var normalized = baseRamp[i] / 65535d;
                var adjusted = peakScale * Math.Pow(normalized, exponent);
                result[i] = (ushort)Math.Round(Math.Min(1d, adjusted) * 65535d);
            }
            return result;
        }
    }

    private readonly struct DisplayDc : IDisposable
    {
        private DisplayDc(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }

        public static DisplayDc? Open(string displayName)
        {
            var handle = CreateDC("DISPLAY", displayName, null, IntPtr.Zero);
            return handle == IntPtr.Zero ? null : new DisplayDc(handle);
        }

        public bool ReadRamp(out ushort[] ramp)
        {
            ramp = new ushort[768];
            return GetDeviceGammaRamp(Handle, ramp);
        }

        public bool SetRamp(ushort[] ramp) => SetDeviceGammaRamp(Handle, ramp);
        public void Dispose() => DeleteDC(Handle);
    }

    private sealed record WmiMonitorRecord(
        string Key,
        string InstanceName,
        string FriendlyName,
        string Manufacturer,
        string Product,
        string Serial,
        bool Active);

    private sealed record WmiBrightnessRecord(int Current, bool CanSet);
    private sealed record GdiTarget(string DeviceName, string DeviceId, string DeviceString);
    private sealed record PhysicalMonitorEntry(string DeviceName, PhysicalMonitor Monitor)
    {
        public IntPtr Handle => Monitor.Handle;
    }

    private readonly record struct DdcBrightness(
        int Minimum, int Current, int Maximum, byte VcpCode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public const int AttachedToDesktop = 0x1;
        public const uint EddGetDeviceInterfaceName = 0x1;
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;

        public static DisplayDevice Create() => new()
        {
            Size = Marshal.SizeOf<DisplayDevice>(),
            DeviceName = "",
            DeviceString = "",
            DeviceID = "",
            DeviceKey = ""
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public static MonitorInfoEx Create() => new()
        {
            Size = Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = ""
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool MonitorEnumDelegate(
        IntPtr monitor, IntPtr deviceContext, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext, IntPtr clip, MonitorEnumDelegate callback, IntPtr data);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(
        IntPtr monitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);

    [DllImport(
        "dxva2.dll",
        EntryPoint = "GetVCPFeatureAndVCPFeatureReply",
        SetLastError = true)]
    private static extern bool GetVcpFeatureAndVcpFeatureReply(
        IntPtr monitor, byte code, out uint type, out uint current, out uint maximum);

    [DllImport("dxva2.dll", EntryPoint = "SetVCPFeature", SetLastError = true)]
    private static extern bool SetVcpFeature(IntPtr monitor, byte code, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitor(IntPtr monitor);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(
        string driver, string device, string? port, IntPtr initializationData);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool GetDeviceGammaRamp(IntPtr deviceContext, ushort[] ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetDeviceGammaRamp(IntPtr deviceContext, ushort[] ramp);
}

public sealed record DisplayBrightness(
    string Id,
    string Name,
    int Current,
    int Minimum,
    int Maximum,
    string Transport,
    bool IsActive,
    bool SupportsHardwareControl,
    bool Writable,
    string PnpId,
    string? DeviceName,
    string? Error);
