using System.Management;

namespace LocalDeck.App.Services;

public sealed class DriverService
{
    public IReadOnlyList<DriverInfo> GetInstalledDrivers()
    {
        var result = new List<DriverInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT DeviceName,Manufacturer,DriverVersion,DriverDate,InfName,DeviceID,DeviceClass " +
                "FROM Win32_PnPSignedDriver");
            using var collection = searcher.Get();
            foreach (ManagementObject item in collection)
            {
                result.Add(new DriverInfo(
                    item["DeviceName"]?.ToString() ?? "",
                    item["Manufacturer"]?.ToString() ?? "",
                    item["DriverVersion"]?.ToString() ?? "",
                    item["DriverDate"]?.ToString() ?? "",
                    item["InfName"]?.ToString() ?? "",
                    item["DeviceID"]?.ToString() ?? "",
                    item["DeviceClass"]?.ToString() ?? ""));
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Driver enumeration failed: {ex.Message}");
        }

        return result
            .Where(item => !string.IsNullOrWhiteSpace(item.DeviceName))
            .OrderBy(item => item.Class)
            .ThenBy(item => item.DeviceName)
            .ToList();
    }

    public Task<IReadOnlyList<DriverUpdateInfo>> CheckWindowsUpdateDriversAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DriverUpdateInfo>>(() =>
        {
            var result = new List<DriverUpdateInfo>();
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                ?? throw new InvalidOperationException("Windows Update COM service is unavailable.");
            dynamic session = Activator.CreateInstance(type)!;
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic updates = searcher.Search("IsInstalled=0 AND Type='Driver'");
            for (var i = 0; i < Convert.ToInt32(updates.Count); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic update = updates.Item(i);
                result.Add(new DriverUpdateInfo(
                    (string)update.Title,
                    update.DriverManufacturer?.ToString() ?? "",
                    update.DriverModel?.ToString() ?? "",
                    update.DriverVerDate?.ToString("O") ?? "",
                    Convert.ToBoolean(update.RebootRequired)));
            }
            return result;
        }, cancellationToken);
    }
}

public sealed record DriverInfo(
    string DeviceName,
    string Manufacturer,
    string Version,
    string Date,
    string InfName,
    string DeviceId,
    string Class);

public sealed record DriverUpdateInfo(
    string Title,
    string Manufacturer,
    string Model,
    string DriverDate,
    bool RebootRequired);
