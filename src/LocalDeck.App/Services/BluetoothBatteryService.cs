using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace LocalDeck.App.Services;

public sealed class BluetoothBatteryService
{
    private static readonly Guid BatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    public async Task<IReadOnlyList<BluetoothBatteryInfo>> GetConnectedBatteryLevelsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<BluetoothBatteryInfo>();
        var selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(
            BluetoothConnectionStatus.Connected);
        var devices = await DeviceInformation.FindAllAsync(selector);
        foreach (var information in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BluetoothLEDevice? device = null;
            try
            {
                device = await BluetoothLEDevice.FromIdAsync(information.Id);
                if (device is null)
                {
                    continue;
                }

                var services = await device.GetGattServicesForUuidAsync(
                    BatteryServiceUuid, BluetoothCacheMode.Uncached);
                if (services.Status != GattCommunicationStatus.Success ||
                    services.Services.Count == 0)
                {
                    continue;
                }

                var service = services.Services[0];
                var characteristics = await service.GetCharacteristicsForUuidAsync(
                    BatteryLevelUuid, BluetoothCacheMode.Uncached);
                if (characteristics.Status != GattCommunicationStatus.Success ||
                    characteristics.Characteristics.Count == 0)
                {
                    continue;
                }

                var read = await characteristics.Characteristics[0].ReadValueAsync(
                    BluetoothCacheMode.Uncached);
                if (read.Status != GattCommunicationStatus.Success || read.Value is null)
                {
                    continue;
                }

                using var reader = DataReader.FromBuffer(read.Value);
                var level = reader.ReadByte();
                result.Add(new BluetoothBatteryInfo(
                    information.Name,
                    device.BluetoothAddress,
                    level));
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Bluetooth battery read failed for {information.Name}: {ex.Message}");
            }
            finally
            {
                device?.Dispose();
            }
        }

        return result;
    }
}

public sealed record BluetoothBatteryInfo(
    string Name,
    ulong Address,
    int BatteryPercent);
