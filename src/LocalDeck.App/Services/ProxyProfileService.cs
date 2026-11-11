using System.Globalization;
using System.Text;
using LocalDeck.App.Models;
using YamlDotNet.Serialization;

namespace LocalDeck.App.Services;

public sealed class ProxyProfileService
{
    private readonly object _sync = new();
    private ProxyProfile? _profile;
    private DateTimeOffset? _loadedAt;

    internal ProxyProfile? Current
    {
        get
        {
            lock (_sync)
            {
                return _profile;
            }
        }
    }

    public DateTimeOffset? LoadedAt
    {
        get
        {
            lock (_sync)
            {
                return _loadedAt;
            }
        }
    }

    public string Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("代理配置文件不存在。", sourcePath);
        }

        AppPaths.EnsureCreated();
        var destination = Path.Combine(AppPaths.ProfileDirectory, "active.yaml");
        File.Copy(sourcePath, destination, overwrite: true);
        Load(destination);
        return destination;
    }

    public bool TryLoad(string? path) => Load(path) is not null;

    internal ProxyProfile? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            lock (_sync)
            {
                _profile = null;
                _loadedAt = null;
                return null;
            }
        }

        var profile = Parse(path);
        lock (_sync)
        {
            _profile = profile;
            _loadedAt = DateTimeOffset.Now;
        }
        return profile;
    }

    public IReadOnlyList<ProxyNodeInfo> GetNodeInfos()
    {
        var profile = Current;
        if (profile is null)
        {
            return [];
        }

        return profile.Nodes.Select(node => new ProxyNodeInfo(
            node.Name,
            node.Type,
            node.Supported,
            node.Supported
                ? node.Type is "hysteria2" or "hy2" && !Hysteria2EngineService.IsEngineAvailable
                    ? "Hysteria2 引擎文件缺失，请重新安装 LocalDeck 发布包。"
                    : null
                : "当前内置核心支持 ss、trojan、hysteria2、http、socks5；该协议尚未实现。"))
            .ToList();
    }

    private static ProxyProfile Parse(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();
        var raw = deserializer.Deserialize<object?>(text);
        var root = AsMap(raw);

        var proxies = new List<ProxyNodeConfig>();
        foreach (var item in AsList(Get(root, "proxies") ?? Get(root, "Proxy")))
        {
            var map = AsMap(item);
            var name = StringValue(map, "name");
            var type = StringValue(map, "type").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            proxies.Add(new ProxyNodeConfig
            {
                Name = name,
                Type = type,
                Server = StringValue(map, "server"),
                Port = IntValue(map, "port"),
                Cipher = StringValue(map, "cipher"),
                Password = StringValue(map, "password"),
                Username = StringValue(map, "username"),
                Sni = FirstNonEmpty(StringValue(map, "sni"), StringValue(map, "servername")),
                Obfs = StringValue(map, "obfs"),
                ObfsPassword = FirstNonEmpty(
                    StringValue(map, "obfs-password"),
                    StringValue(map, "obfs_password")),
                Fingerprint = FirstNonEmpty(
                    StringValue(map, "fingerprint"),
                    StringValue(map, "pinSHA256")),
                Up = StringValue(map, "up"),
                Down = StringValue(map, "down"),
                SkipCertificateValidation = BoolValue(map, "skip-cert-verify")
            });
        }

        var groups = new List<ProxyGroupConfig>();
        foreach (var item in AsList(Get(root, "proxy-groups") ?? Get(root, "proxy_groups")))
        {
            var map = AsMap(item);
            var name = StringValue(map, "name");
            var type = StringValue(map, "type").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            groups.Add(new ProxyGroupConfig
            {
                Name = name,
                Type = type,
                Proxies = AsList(Get(map, "proxies")).Select(StringFromObject).Where(x => x.Length > 0).ToList()
            });
        }

        var rules = AsList(Get(root, "rules")).Select(StringFromObject)
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .ToList();
        var final = rules.FirstOrDefault(rule =>
            rule.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase) ||
            rule.StartsWith("FINAL,", StringComparison.OrdinalIgnoreCase))?.Split(',')[1].Trim() ?? "DIRECT";

        return new ProxyProfile
        {
            Port = FirstPositive(IntValue(root, "mixed-port"), IntValue(root, "port"), 7897),
            Mode = FirstNonEmpty(StringValue(root, "mode"), "rule"),
            Nodes = proxies,
            Groups = groups,
            Rules = rules,
            FinalPolicy = final
        };
    }

    private static Dictionary<string, object?> AsMap(object? value) =>
        value is IDictionary<object, object> dictionary
            ? dictionary.ToDictionary(
                pair => pair.Key?.ToString() ?? "",
                pair => (object?)pair.Value,
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    private static List<object?> AsList(object? value) =>
        value is IEnumerable<object> sequence && value is not string
            ? sequence.Select(item => (object?)item).ToList()
            : [];

    private static object? Get(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value : null;

    private static string StringValue(Dictionary<string, object?> map, string key) =>
        StringFromObject(Get(map, key));

    private static string StringFromObject(object? value) =>
        value is null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";

    private static int IntValue(Dictionary<string, object?> map, string key) =>
        int.TryParse(StringValue(map, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static bool BoolValue(Dictionary<string, object?> map, string key) =>
        bool.TryParse(StringValue(map, key), out var value) && value;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static int FirstPositive(params int[] values) =>
        values.FirstOrDefault(value => value > 0);
}
