using System.Diagnostics;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalDeck.App.Services;

public sealed partial class GpuUnlockService
{
    private const string UpstreamUrl = "https://github.com/jatinop7/gpu-unlock-windows";
    private const string UpstreamVersion = "0.1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly GpuUnlockProfile[] Profiles =
    [
        new(
            "acer-anv15-41-rtx4050-28a1",
            "Acer Nitro V 15 ANV15-41 / RTX 4050 Laptop",
            "10DE",
            "28A1",
            "1025",
            "1758",
            "Acer Nitro V 15 ANV15-41",
            false,
            "measurement-recorded",
            "公开 registry 仍将硬件写入标记为关闭；必须完成精确机型恢复路径审查。")
    ];

    public GpuUnlockStatus GetStatus()
    {
        var identity = ReadGpuIdentity();
        var profile = Profiles.FirstOrDefault(item =>
            item.VendorId.Equals(identity.VendorId, StringComparison.OrdinalIgnoreCase) &&
            item.DeviceId.Equals(identity.DeviceId, StringComparison.OrdinalIgnoreCase) &&
            item.SubsystemVendorId.Equals(identity.SubsystemVendorId, StringComparison.OrdinalIgnoreCase) &&
            item.SubsystemDeviceId.Equals(identity.SubsystemDeviceId, StringComparison.OrdinalIgnoreCase));
        var nvidia = identity.VendorId.Equals("10DE", StringComparison.OrdinalIgnoreCase);
        var supportedGeneration = identity.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase);
        var detail = !nvidia
            ? "当前主显卡不是 NVIDIA，不适用该项目。"
            : profile is null
                ? "可采集设备指纹和兼容报告，但当前公开 profile 不包含这台 GPU/子系统组合。"
                : profile.WriteEnabled
                    ? "已找到启用写入的精确 profile。"
                    : "已找到精确 profile，但上游仍将硬件写入标记为关闭。";

        return new GpuUnlockStatus(
            identity.Name,
            identity.PnpDeviceId,
            identity.VendorId,
            identity.DeviceId,
            identity.SubsystemVendorId,
            identity.SubsystemDeviceId,
            identity.DriverVersion,
            identity.SystemModel,
            nvidia && supportedGeneration,
            profile?.ProfileId,
            profile?.WriteEnabled ?? false,
            profile?.Status ?? "no-matching-profile",
            UpstreamVersion,
            UpstreamUrl,
            "硬件解锁发生在 Windows 驱动加载前的 UEFI 阶段。LocalDeck 不会用用户态寄存器写入替代该流程。",
            detail);
    }

    public string ExportCompatibilityReport()
    {
        var status = GetStatus();
        var directory = Path.Combine(AppPaths.DataDirectory, "gpu-unlock");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"compatibility-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var report = new
        {
            schemaVersion = 1,
            generatedAt = DateTimeOffset.Now,
            source = UpstreamUrl,
            privacy = "No hostname, username, serial number, UUID, or MAC address.",
            gpu = status,
            recommendation = status.ProfileWriteEnabled
                ? "Review the exact upstream recovery procedure before any UEFI write."
                : "Submit this sanitized report upstream. Do not change profile IDs or run another machine's EFI writer."
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
        return path;
    }

    public void OpenUpstream()
    {
        Process.Start(new ProcessStartInfo(UpstreamUrl) { UseShellExecute = true });
    }

    public static void OpenFolder(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"")
            {
                UseShellExecute = true
            });
        }
    }

    private static GpuIdentity ReadGpuIdentity()
    {
        var systemModel = ReadSystemModel();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController");
            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                var name = item["Name"]?.ToString()?.Trim() ?? "GPU";
                var pnp = item["PNPDeviceID"]?.ToString()?.Trim() ?? "";
                var driver = item["DriverVersion"]?.ToString()?.Trim() ?? "";
                var match = PciIdentityRegex().Match(pnp);
                if (!match.Success)
                {
                    continue;
                }

                // PNP SUBSYS is printed as device-id followed by vendor-id.
                return new GpuIdentity(
                    name,
                    pnp,
                    match.Groups["vendor"].Value.ToUpperInvariant(),
                    match.Groups["device"].Value.ToUpperInvariant(),
                    match.Groups["subvendor"].Value.ToUpperInvariant(),
                    match.Groups["subdevice"].Value.ToUpperInvariant(),
                    driver,
                    systemModel);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"GPU unlock inventory failed: {ex.Message}");
        }

        return new GpuIdentity("Unknown GPU", "", "", "", "", "", "", systemModel);
    }

    private static string ReadSystemModel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                return $"{item["Manufacturer"]} {item["Model"]}".Trim();
            }
        }
        catch
        {
            // The FPGA/PCI identity remains usable without the OEM model.
        }
        return "Unknown";
    }

    [GeneratedRegex(
        @"VEN_(?<vendor>[0-9A-F]{4})&DEV_(?<device>[0-9A-F]{4})&SUBSYS_(?<subdevice>[0-9A-F]{4})(?<subvendor>[0-9A-F]{4})",
        RegexOptions.IgnoreCase)]
    private static partial Regex PciIdentityRegex();

    private sealed record GpuIdentity(
        string Name,
        string PnpDeviceId,
        string VendorId,
        string DeviceId,
        string SubsystemVendorId,
        string SubsystemDeviceId,
        string DriverVersion,
        string SystemModel);

    private sealed record GpuUnlockProfile(
        string ProfileId,
        string Name,
        string VendorId,
        string DeviceId,
        string SubsystemVendorId,
        string SubsystemDeviceId,
        string SystemProductName,
        bool WriteEnabled,
        string Status,
        string Detail);
}

public sealed record GpuUnlockStatus(
    string Name,
    string PnpDeviceId,
    string VendorId,
    string DeviceId,
    string SubsystemVendorId,
    string SubsystemDeviceId,
    string DriverVersion,
    string SystemModel,
    bool InventorySupported,
    string? MatchedProfileId,
    bool ProfileWriteEnabled,
    string ProfileStatus,
    string UpstreamVersion,
    string UpstreamUrl,
    string Requirement,
    string Detail);
