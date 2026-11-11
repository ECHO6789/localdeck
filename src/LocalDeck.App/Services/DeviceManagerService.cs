using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace LocalDeck.App.Services;

public sealed class DeviceManagerService
{
    public IReadOnlyList<PeripheralDevice> GetDevices()
    {
        var result = new List<PeripheralDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT DeviceID,Name,PNPClass,Manufacturer,Status,ConfigManagerErrorCode,Service,HardwareID " +
                "FROM Win32_PnPEntity");
            using var collection = searcher.Get();
            foreach (ManagementObject item in collection)
            {
                var className = item["PNPClass"]?.ToString() ?? "";
                if (!IsPeripheralClass(className))
                {
                    continue;
                }

                var instanceId = item["DeviceID"]?.ToString() ?? "";
                var name = item["Name"]?.ToString() ?? "未知设备";
                var problem = Convert.ToInt32(item["ConfigManagerErrorCode"] ?? 0);
                var canToggle = CanSafelyToggle(className, name);
                result.Add(new PeripheralDevice(
                    instanceId,
                    name,
                    className,
                    item["Manufacturer"]?.ToString() ?? "",
                    problem == 22 ? "已禁用" : problem == 0 ? "正常" : $"错误 {problem}",
                    problem,
                    item["Service"]?.ToString() ?? "",
                    canToggle));
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Device enumeration failed: {ex.Message}");
        }

        return result
            .DistinctBy(item => item.InstanceId)
            .OrderBy(item => item.Class)
            .ThenBy(item => item.Name)
            .ToList();
    }

    public bool SetEnabled(string instanceId, bool enabled)
    {
        var device = GetDevices().FirstOrDefault(item =>
            item.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        if (device is null || !device.CanToggle)
        {
            return false;
        }

        var status = CM_Locate_DevNodeW(out var node, instanceId, 0);
        if (status != 0)
        {
            return false;
        }

        status = enabled ? CM_Enable_DevNode(node, 0) : CM_Disable_DevNode(node, 0);
        return status == 0;
    }

    public bool Rescan()
    {
        return CM_Reenumerate_DevNode(0, 0) == 0;
    }

    public void OpenDeviceManager()
    {
        Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
    }

    public void OpenProperties(string instanceId)
    {
        Process.Start(new ProcessStartInfo(
            "rundll32.exe",
            $"devmgr.dll,DeviceProperties_RunDLL /DeviceID \"{instanceId}\"")
        {
            UseShellExecute = true
        });
    }

    private static bool IsPeripheralClass(string className)
    {
        return className is "Mouse" or "Keyboard" or "HIDClass" or "Monitor" or
            "AudioEndpoint" or "Media" or "Camera" or "Bluetooth" or "USB" or "Battery";
    }

    private static bool CanSafelyToggle(string className, string name)
    {
        if (className is "Mouse" or "Keyboard" or "HIDClass" or "Monitor" or "Battery")
        {
            return false;
        }

        return !name.Contains("Root Hub", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Host Controller", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("System", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(
        out uint deviceNode, string deviceInstanceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Enable_DevNode(uint deviceNode, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Disable_DevNode(uint deviceNode, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Reenumerate_DevNode(uint deviceNode, uint flags);
}

public sealed record PeripheralDevice(
    string InstanceId,
    string Name,
    string Class,
    string Manufacturer,
    string Status,
    int ProblemCode,
    string Service,
    bool CanToggle);
