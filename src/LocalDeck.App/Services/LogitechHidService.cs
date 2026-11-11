using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using HidSharp;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace LocalDeck.App.Services;

public sealed class LogitechHidService
{
    private const int LogitechVendorId = 0x046D;
    private const byte HidppShortReportId = 0x10;
    private const byte HidppLongReportId = 0x11;
    private readonly object _sync = new();

    public IReadOnlyList<LogitechDevice> GetDevices()
    {
        lock (_sync)
        {
            var result = new List<LogitechDevice>();
            var cached = ReadLgHubSnapshot();
            foreach (var candidate in FindCandidates())
            {
                try
                {
                    using var stream = NativeHidTransport.Open(candidate.Device.DevicePath);
                    if (!stream.IsOpen)
                    {
                        continue;
                    }
                    var probe = Probe(stream);
                    if (probe is null)
                    {
                        continue;
                    }

                    var battery = ReadBattery(
                        stream, probe.DeviceIndex, probe.BatteryFeature, probe.BatteryFeatureId);
                    var dpi = ReadDpi(stream, probe.DeviceIndex, probe.DpiFeature);
                    var name = ReadDeviceName(
                        stream, probe.DeviceIndex, probe.DeviceNameFeature) ??
                        candidate.Device.GetProductName();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = cached?.DeviceName ??
                               (candidate.Device.ProductID == 0xC539
                                   ? "Logitech Lightspeed Receiver"
                                   : "Logitech HID++ 设备");
                    }
                    else if (name.Equals("Logitech HID++ 设备", StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrWhiteSpace(cached?.DeviceName))
                    {
                        name = cached.DeviceName;
                    }
                    else if (name.StartsWith("502 LIGHTSPEED", StringComparison.OrdinalIgnoreCase))
                    {
                        name = "G" + name;
                    }

                    result.Add(new LogitechDevice(
                        AppPaths.StableId(candidate.Device.DevicePath),
                        name,
                        candidate.Device.ProductID,
                        battery.Percent,
                        battery.Charging,
                        dpi,
                        probe.DeviceIndex,
                        true,
                        battery.Percent is not null || dpi is not null,
                        null));
                    AppPaths.Log(
                        $"HID++ direct device: index={probe.DeviceIndex}, " +
                        $"battery={probe.BatteryFeatureId?.ToString("X4") ?? "none"}, " +
                        $"dpi={probe.DpiFeatureId?.ToString("X4") ?? "none"}, " +
                        $"product={candidate.Device.ProductID:X4}.");
                    if (battery.Percent is null && dpi is null)
                    {
                        AppPaths.Log(
                            $"HID++ device {name}, index {probe.DeviceIndex}: no battery/DPI feature " +
                            $"(battery={probe.BatteryFeature?.ToString() ?? "none"}, dpi={probe.DpiFeature?.ToString() ?? "none"}).");
                    }
                }
                catch (Exception ex)
                {
                    AppPaths.Log($"Logitech HID candidate failed: {ex.Message}");
                }
            }

            if (result.Count == 0)
            {
                result.Add(cached is null
                    ? new LogitechDevice(
                        "unavailable", "未检测到可访问的 Logitech HID++ 设备", 0,
                        null, false, null, null, false, false,
                        "LGHUB 或另一个进程可能占用了设备；也可检查接收器连接。")
                    : new LogitechDevice(
                        $"lghub:{cached.DeviceKey}",
                        cached.DeviceName,
                        0,
                        cached.BatteryPercent,
                        false,
                        cached.Dpi,
                        null,
                        true,
                        false,
                        "只读，数据来自 LGHUB 本地缓存；HID++ 直写接口当前不可用。"));
            }

            return result;
        }
    }

    public bool SetDpi(string deviceId, int dpi)
    {
        dpi = Math.Clamp(dpi, 100, 32_000);
        lock (_sync)
        {
            foreach (var candidate in FindCandidates())
            {
                if (AppPaths.StableId(candidate.Device.DevicePath) != deviceId)
                {
                    continue;
                }

                try
                {
                    using var stream = NativeHidTransport.Open(candidate.Device.DevicePath);
                    if (!stream.IsOpen)
                    {
                        continue;
                    }
                    var probe = Probe(stream);
                    if (probe?.DpiFeature is null)
                    {
                        return false;
                    }

                    if (probe.DpiFeatureId == 0x2202)
                    {
                        var payload = new byte[3];
                        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)dpi);
                        return Request(
                            stream, probe.DeviceIndex, probe.DpiFeature.Value, 0x40, payload) is not null;
                    }
                    else
                    {
                        var payload = new byte[3];
                        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)dpi);
                        return Request(
                            stream, probe.DeviceIndex, probe.DpiFeature.Value, 0x30, payload) is not null;
                    }
                }
                catch (Exception ex)
                {
                    AppPaths.Log($"Logitech DPI update failed: {ex.Message}");
                    return false;
                }
            }
        }

        return false;
    }

    private static int? ReadDpi(NativeHidTransport stream, byte deviceIndex, byte? featureIndex)
    {
        if (featureIndex is null)
        {
            return null;
        }

        try
        {
            var response = Request(stream, deviceIndex, featureIndex.Value, 0x20, [0]);
            if (response is null || response.Payload.Length < 4)
            {
                return null;
            }

            var current = BinaryPrimitives.ReadUInt16BigEndian(response.Payload.AsSpan(1, 2));
            return current == 0
                ? BinaryPrimitives.ReadUInt16BigEndian(response.Payload.AsSpan(3, 2))
                : current;
        }
        catch
        {
            return null;
        }
    }

    private static (int? Percent, bool Charging) ReadBattery(
        NativeHidTransport stream, byte deviceIndex, byte? featureIndex, ushort? featureId)
    {
        if (featureIndex is null)
        {
            return (null, false);
        }

        try
        {
            var function = featureId == 0x1004 ? (byte)0x10 : (byte)0x00;
            var response = Request(stream, deviceIndex, featureIndex.Value, function);
            if (response is null || response.Payload.Length == 0)
            {
                return (null, false);
            }

            if (featureId == 0x1001 && response.Payload.Length >= 3)
            {
                var voltage = BinaryPrimitives.ReadUInt16BigEndian(response.Payload);
                var flags = response.Payload[2];
                return (EstimateBatteryPercent(voltage), (flags & 0x80) != 0);
            }

            var level = response.Payload[0];
            var charging = featureId switch
            {
                0x1000 => response.Payload.Length > 1 && (response.Payload[1] & 0x02) != 0,
                0x1004 => response.Payload.Length > 2 && response.Payload[2] is 1 or 2 or 3 or 4,
                _ => false
            };
            return level is >= 0 and <= 100 ? (level, charging) : (null, charging);
        }
        catch
        {
            return (null, false);
        }
    }

    private static string? ReadDeviceName(
        NativeHidTransport stream, byte deviceIndex, byte? featureIndex)
    {
        if (featureIndex is null)
        {
            return null;
        }
        try
        {
            var count = Request(stream, deviceIndex, featureIndex.Value, 0x00);
            if (count is null || count.Payload.Length == 0 || count.Payload[0] == 0)
            {
                return null;
            }

            var totalLength = count.Payload[0];
            var name = new List<byte>(totalLength);
            var offset = 0;
            while (offset < totalLength)
            {
                var chunk = Request(
                    stream, deviceIndex, featureIndex.Value, 0x10, [(byte)offset]);
                if (chunk is null || chunk.Payload.Length < 2 || chunk.Payload[0] == 0)
                {
                    break;
                }
                var length = Math.Min(
                    chunk.Payload[0],
                    Math.Min(chunk.Payload.Length - 1, totalLength - offset));
                name.AddRange(chunk.Payload.AsSpan(1, length).ToArray());
                offset += length;
            }
            return name.Count == 0
                ? null
                : System.Text.Encoding.UTF8.GetString(name.ToArray()).Trim('\0', ' ');
        }
        catch
        {
            return null;
        }
    }

    private static int EstimateBatteryPercent(int millivolts)
    {
        (int Voltage, int Percent)[] curve =
        [
            (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60),
            (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10),
            (3646, 5), (3579, 2), (3500, 0)
        ];
        if (millivolts >= curve[0].Voltage)
        {
            return 100;
        }
        if (millivolts <= curve[^1].Voltage)
        {
            return 0;
        }
        for (var i = 0; i < curve.Length - 1; i++)
        {
            var high = curve[i];
            var low = curve[i + 1];
            if (millivolts >= low.Voltage && millivolts <= high.Voltage)
            {
                return (int)Math.Round(
                    low.Percent +
                    (high.Percent - low.Percent) *
                    (millivolts - low.Voltage) / (double)(high.Voltage - low.Voltage));
            }
        }
        return 0;
    }

    private static ProbeResult? Probe(NativeHidTransport stream)
    {
        ProbeResult? best = null;
        var bestScore = -1;
        for (byte deviceIndex = 1; deviceIndex <= 6; deviceIndex++)
        {
            var features = DiscoverFeatures(stream, deviceIndex);
            byte? Get(ushort id) => features.TryGetValue(id, out var index) ? index : null;

            var batteryLegacyFeature = Get(0x1000);
            var batteryUnifiedFeature = Get(0x1004);
            var batteryVoltageFeature = Get(0x1001);
            var dpiFeature = Get(0x2201);
            var extendedDpiFeature = Get(0x2202);
            var deviceNameFeature = Get(0x0005);
            var batteryFeature = batteryUnifiedFeature ?? batteryLegacyFeature ?? batteryVoltageFeature;
            var batteryFeatureId = batteryUnifiedFeature is not null ? (ushort?)0x1004
                : batteryLegacyFeature is not null ? (ushort?)0x1000
                : batteryVoltageFeature is not null ? (ushort?)0x1001
                : null;
            var dpiFeatureId = dpiFeature is not null ? (ushort?)0x2201
                : extendedDpiFeature is not null ? (ushort?)0x2202
                : null;
            dpiFeature ??= extendedDpiFeature;
            var score = (batteryFeature is not null ? 4 : 0) +
                        (dpiFeature is not null ? 4 : 0) +
                        (deviceNameFeature is not null ? 1 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = new ProbeResult(
                    deviceIndex, batteryFeature, batteryFeatureId, dpiFeature, dpiFeatureId, deviceNameFeature);
            }
            if (score >= 8)
            {
                return best;
            }
        }

        return bestScore > 0 ? best : null;
    }

    private static Dictionary<ushort, byte> DiscoverFeatures(
        NativeHidTransport stream, byte deviceIndex)
    {
        var features = new Dictionary<ushort, byte>();
        try
        {
            var featureSetRequest = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(featureSetRequest, 0x0001);
            var featureSet = Request(stream, deviceIndex, 0, 0, featureSetRequest);
            if (featureSet is null || featureSet.Payload.Length == 0 || featureSet.Payload[0] == 0)
            {
                return features;
            }

            var featureSetIndex = featureSet.Payload[0];
            features[0x0001] = featureSetIndex;
            var countResponse = Request(stream, deviceIndex, featureSetIndex, 0);
            if (countResponse is null || countResponse.Payload.Length == 0)
            {
                return features;
            }

            var count = Math.Min(countResponse.Payload[0], (byte)32);
            for (byte index = 0; index < count; index++)
            {
                var item = Request(stream, deviceIndex, featureSetIndex, 0x10, [index]);
                if (item is null || item.Payload.Length < 2)
                {
                    continue;
                }

                var featureId = BinaryPrimitives.ReadUInt16BigEndian(item.Payload);
                if (featureId != 0 && !features.ContainsKey(featureId))
                {
                    features[featureId] = index;
                }
            }
        }
        catch
        {
            // Return everything discovered before the device stopped responding.
        }

        return features;
    }

    private static HidResponse? Request(
        NativeHidTransport stream, byte deviceIndex, byte featureIndex,
        byte function, byte[]? payload = null)
    {
        var outputLength = Math.Max(7, stream.OutputReportLength);
        var report = new byte[outputLength];
        const byte softwareId = 0x08;
        report[0] = HidppLongReportId;
        report[1] = deviceIndex;
        report[2] = featureIndex;
        report[3] = (byte)((function & 0xF0) | softwareId);
        if (payload is not null)
        {
            Array.Copy(payload, 0, report, 4, Math.Min(payload.Length, outputLength - 4));
        }

        if (!stream.Write(report))
        {
            return null;
        }

        var expectedFunction = (byte)((function & 0xF0) | softwareId);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var input = stream.TryRead(1800);
            if (input is null)
            {
                return null;
            }

            if (input.Length < 7 ||
                input[0] is not (HidppShortReportId or HidppLongReportId))
            {
                continue;
            }

            if (input[1] == deviceIndex &&
                input[2] == featureIndex &&
                input[3] == expectedFunction)
            {
                return new HidResponse(input.AsSpan(4, input.Length - 4).ToArray());
            }
        }

        return null;
    }

    private static IEnumerable<HidCandidate> FindCandidates()
    {
        foreach (var device in DeviceList.Local
                     .GetHidDevices(LogitechVendorId, null, null, null)
                     .OrderBy(item => item.GetMaxInputReportLength() >= 20 ? 0 : 1))
        {
            if (device.ProductID == 0xC232 ||
                device.GetMaxInputReportLength() < 7 ||
                device.GetMaxOutputReportLength() < 7)
            {
                continue;
            }
            yield return new HidCandidate(device);
        }
    }

    private static LgHubSnapshot? ReadLgHubSnapshot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var database = Path.Combine(local, "LGHUB", "settings.db");
        if (!File.Exists(database))
        {
            return null;
        }

        try
        {
            using var connection = new SqliteConnection(
                $"Data Source={database};Mode=ReadOnly;Cache=Private");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT file FROM data ORDER BY _id DESC LIMIT 1;";
            if (command.ExecuteScalar() is not byte[] bytes)
            {
                return null;
            }

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            string? deviceKey = null;
            int? battery = null;
            DateTimeOffset? updated = null;
            foreach (var property in root.EnumerateObject())
            {
                var isPercentage = property.Name.EndsWith(
                    "/percentage", StringComparison.OrdinalIgnoreCase);
                var isWarning = property.Name.EndsWith(
                    "/warning", StringComparison.OrdinalIgnoreCase);
                if (!property.Name.StartsWith("battery/", StringComparison.OrdinalIgnoreCase) ||
                    (!isPercentage && !isWarning) ||
                    property.Value.ValueKind != JsonValueKind.Object ||
                    !property.Value.TryGetProperty("percentage", out var percentage) ||
                    !percentage.TryGetInt32(out var percent))
                {
                    continue;
                }

                deviceKey ??= property.Name.Split('/')[1];
                if (battery is null || isPercentage)
                {
                    battery = percent;
                }
                if (property.Value.TryGetProperty("time", out var time) &&
                    DateTimeOffset.TryParse(time.GetString(), out var timestamp))
                {
                    updated = timestamp;
                }
            }

            int? dpi = null;
            if (root.TryGetProperty("cards", out var cards) &&
                cards.ValueKind == JsonValueKind.Object &&
                cards.TryGetProperty("cards", out var array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                foreach (var card in array.EnumerateArray())
                {
                    if (!card.TryGetProperty("mouseSettings", out var mouse) ||
                        !mouse.TryGetProperty("dpiTable", out var dpiTable) ||
                        !dpiTable.TryGetProperty("activeDpi", out var activeDpi) ||
                        !activeDpi.TryGetInt32(out var value))
                    {
                        continue;
                    }

                    var readOnly = card.TryGetProperty("readOnly", out var readOnlyValue) &&
                                   readOnlyValue.ValueKind == JsonValueKind.True;
                    if (!readOnly)
                    {
                        dpi = value;
                    }
                }
            }

            if (deviceKey is null)
            {
                return null;
            }

            return new LgHubSnapshot(
                deviceKey,
                deviceKey.Replace("wireless", " Wireless", StringComparison.OrdinalIgnoreCase).ToUpperInvariant(),
                battery,
                dpi,
                updated);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"LGHUB local cache read failed: {ex.Message}");
            return null;
        }
    }

    private sealed record LgHubSnapshot(
        string DeviceKey,
        string DeviceName,
        int? BatteryPercent,
        int? Dpi,
        DateTimeOffset? UpdatedAt);

    private sealed record ProbeResult(
        byte DeviceIndex,
        byte? BatteryFeature,
        ushort? BatteryFeatureId,
        byte? DpiFeature,
        ushort? DpiFeatureId,
        byte? DeviceNameFeature);

    private sealed record HidResponse(byte[] Payload);

    private sealed record HidCandidate(HidDevice Device);

    private sealed class NativeHidTransport : IDisposable
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private readonly SafeFileHandle _handle;

        private NativeHidTransport(
            SafeFileHandle handle, int inputReportLength, int outputReportLength)
        {
            _handle = handle;
            InputReportLength = Math.Max(20, inputReportLength);
            OutputReportLength = Math.Max(7, outputReportLength);
        }

        public int InputReportLength { get; }
        public int OutputReportLength { get; }
        public bool IsOpen => !_handle.IsInvalid && !_handle.IsClosed;

        public static NativeHidTransport Open(string path)
        {
            var handle = CreateFileW(
                path,
                GenericRead | GenericWrite,
                ShareRead | ShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            var length = 20;
            if (handle.IsInvalid)
            {
                return new NativeHidTransport(handle, length, length);
            }

            var attributes = DeviceList.Local
                .GetHidDevices()
                .FirstOrDefault(device => device.DevicePath.Equals(
                    path, StringComparison.OrdinalIgnoreCase));
            return new NativeHidTransport(
                handle,
                attributes?.GetMaxInputReportLength() ?? 20,
                attributes?.GetMaxOutputReportLength() ?? 20);
        }

        public bool Write(byte[] report) =>
            WriteFile(
                _handle, report, (uint)report.Length, out var written, IntPtr.Zero) &&
            written == report.Length;

        public byte[]? TryRead(int timeoutMilliseconds)
        {
            if (!IsOpen)
            {
                return null;
            }

            var buffer = new byte[InputReportLength];
            var readTask = Task.Run(() =>
            {
                if (!ReadFile(
                        _handle, buffer, (uint)buffer.Length, out var read, IntPtr.Zero) ||
                    read == 0)
                {
                    return Array.Empty<byte>();
                }
                return buffer.AsSpan(0, (int)read).ToArray();
            });
            if (!readTask.Wait(timeoutMilliseconds))
            {
                CancelIoEx(_handle, IntPtr.Zero);
                try
                {
                    _ = readTask.GetAwaiter().GetResult();
                }
                catch
                {
                    // Cancellation releases the pending synchronous read.
                }
                return null;
            }
            var data = readTask.GetAwaiter().GetResult();
            return data.Length == 0 ? null : data;
        }

        public void Dispose() => _handle.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            SafeFileHandle file, byte[] buffer, uint bytesToWrite,
            out uint bytesWritten, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(
            SafeFileHandle file, byte[] buffer, uint bytesToRead,
            out uint bytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(
            SafeFileHandle file, IntPtr overlapped);
    }
}

public sealed record LogitechDevice(
    string Id,
    string Name,
    int ProductId,
    int? BatteryPercent,
    bool Charging,
    int? Dpi,
    byte? DeviceIndex,
    bool Available,
    bool Writable,
    string? Error);
