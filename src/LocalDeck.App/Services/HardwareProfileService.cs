using System.Management;
using Microsoft.Win32;

namespace LocalDeck.App.Services;

public sealed class HardwareProfileService
{
    private readonly VendorControlService _vendor;
    private readonly object _sync = new();
    private HardwareProfile? _cached;
    private DateTime _cachedAt = DateTime.MinValue;

    public HardwareProfileService(VendorControlService vendor)
    {
        _vendor = vendor;
    }

    public HardwareProfile Read()
    {
        lock (_sync)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(30))
            {
                return _cached;
            }

            var (manufacturer, product, bios) = ReadBoardIdentity();
            var model = ReadComputerModel();
            var identity = $"{manufacturer} {product} {model}".ToUpperInvariant();
            var definition = ResolveDefinition(identity);
            var services = FindInstalledInterfaces(definition.InterfaceHints);
            var vendorTelemetry = definition.FamilyId == "pegatron"
                ? _vendor.ReadTelemetry()
                : null;
            var controlAvailable = definition.FamilyId == "pegatron" &&
                                   vendorTelemetry is { Error: null, Source: not null };
            var capabilities = controlAvailable
                ? new[]
                {
                    "CPU/GPU 温度", "CPU/GPU 风扇转速", "办公/游戏/狂飙风扇模式",
                    "厂商功率档位", "NVIDIA NVML 控制"
                }
                : new[]
                {
                    "主板/机型识别", "Windows 通用监测", "NVIDIA NVML 控制"
                };

            _cached = new HardwareProfile(
                manufacturer,
                product,
                model,
                bios,
                definition.FamilyId,
                definition.FamilyName,
                definition.PanelName,
                definition.ControlKind,
                controlAvailable,
                services,
                capabilities,
                controlAvailable
                    ? $"已连接 {definition.PanelName} 的本地接口。"
                    : services.Count > 0
                        ? $"检测到 {string.Join("、", services)}；通用传感器可用，厂商写入接口未映射。"
                        : $"已识别 {definition.PanelName}；当前系统未发现可直接调用的厂商接口。");
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    private static BoardDefinition ResolveDefinition(string identity)
    {
        if (ContainsAny(identity, "MACHENIKE", "PEGATRON", "X15C", "F117"))
        {
            return new(
                "pegatron",
                "Machenike / Pegatron",
                "GFN Control",
                "vendor-gfn",
                ["PHotkey", "PGFNEX", "Pegatron"]);
        }
        if (ContainsAny(identity, "MICRO-STAR", "MSI"))
        {
            return new("msi", "MSI", "MSI Center", "vendor-msi", ["MSI", "Micro-Star"]);
        }
        if (ContainsAny(identity, "ASUSTEK", "ASUS", "ROG"))
        {
            return new("asus", "ASUS", "Armoury Crate", "vendor-asus", ["ASUS", "Armoury", "ROG"]);
        }
        if (ContainsAny(identity, "GIGABYTE", "AORUS"))
        {
            return new("gigabyte", "GIGABYTE / AORUS", "GIGABYTE Control Center", "vendor-gigabyte",
                ["Gigabyte", "AORUS", "GCC"]);
        }
        if (ContainsAny(identity, "ASROCK"))
        {
            return new("asrock", "ASRock", "ASRock Motherboard Utility", "vendor-asrock",
                ["ASRock", "ASR"]);
        }
        if (ContainsAny(identity, "LENOVO", "LEGION"))
        {
            return new("lenovo", "Lenovo", "Lenovo Legion Space", "vendor-lenovo",
                ["Lenovo", "Legion", "LSA"]);
        }
        if (ContainsAny(identity, "HEWLETT", "HP ", "OMEN"))
        {
            return new("hp", "HP / OMEN", "OMEN Gaming Hub", "vendor-hp",
                ["HP", "OMEN"]);
        }
        if (ContainsAny(identity, "DELL", "ALIENWARE"))
        {
            return new("dell", "Dell / Alienware", "Alienware Command Center", "vendor-dell",
                ["Dell", "Alienware", "AWCC"]);
        }
        if (ContainsAny(identity, "ACER", "PREDATOR"))
        {
            return new("acer", "Acer / Predator", "PredatorSense", "vendor-acer",
                ["Acer", "Predator"]);
        }
        if (ContainsAny(identity, "RAZER"))
        {
            return new("razer", "Razer", "Razer Synapse", "vendor-razer",
                ["Razer", "Synapse"]);
        }
        if (ContainsAny(identity, "TONGFANG", "MECHREVO", "THUNDEROBOT"))
        {
            return new("tongfang", "Tongfang", "Tongfang Control Center", "vendor-tongfang",
                ["Tongfang", "MECHREVO", "Thunderobot"]);
        }
        if (ContainsAny(identity, "CLEVO", "SAGER"))
        {
            return new("clevo", "Clevo / Sager", "Control Center", "vendor-clevo",
                ["Clevo", "Sager", "Control Center"]);
        }
        return new("generic", "Generic", "Windows Hardware", "generic", []);
    }

    private static (string Manufacturer, string Product, string Bios) ReadBoardIdentity()
    {
        var manufacturer = "Unknown";
        var product = "Unknown";
        var bios = "Unknown";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard");
            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                manufacturer = item["Manufacturer"]?.ToString()?.Trim() ?? manufacturer;
                product = item["Product"]?.ToString()?.Trim() ?? product;
                break;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Baseboard detection failed: {ex.Message}");
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                bios = item["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? bios;
                break;
            }
        }
        catch
        {
            // BIOS version is optional display metadata.
        }
        return (manufacturer, product, bios);
    }

    private static string ReadComputerModel()
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
            // Board identity remains available.
        }
        return "";
    }

    private static List<string> FindInstalledInterfaces(IReadOnlyList<string> hints)
    {
        if (hints.Count == 0)
        {
            return [];
        }

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services");
            foreach (var name in services?.GetSubKeyNames() ?? [])
            {
                if (hints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(name);
                }
            }
        }
        catch
        {
            // Registry service enumeration is best-effort.
        }
        return found.OrderBy(name => name).Take(6).ToList();
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private sealed record BoardDefinition(
        string FamilyId,
        string FamilyName,
        string PanelName,
        string ControlKind,
        List<string> InterfaceHints);
}

public sealed record HardwareProfile(
    string Manufacturer,
    string Product,
    string Model,
    string BiosVersion,
    string FamilyId,
    string FamilyName,
    string PanelName,
    string ControlKind,
    bool ControlAvailable,
    IReadOnlyList<string> DetectedInterfaces,
    IReadOnlyList<string> Capabilities,
    string Detail);
