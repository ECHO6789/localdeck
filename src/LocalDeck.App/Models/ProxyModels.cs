namespace LocalDeck.App.Models;

public sealed record ProxyNodeInfo(
    string Name,
    string Type,
    bool Supported,
    string? Warning);

public sealed record ProxyStatus(
    bool Running,
    string ListenAddress,
    int Port,
    string Mode,
    string? SelectedNode,
    string? ProfilePath,
    DateTimeOffset? ProfileLoadedAt,
    IReadOnlyList<ProxyNodeInfo> Nodes,
    bool SystemProxyEnabled,
    string? Error);

internal sealed class ProxyProfile
{
    public int Port { get; init; } = 7897;
    public string Mode { get; init; } = "rule";
    public List<ProxyNodeConfig> Nodes { get; init; } = [];
    public List<ProxyGroupConfig> Groups { get; init; } = [];
    public List<string> Rules { get; init; } = [];
    public string FinalPolicy { get; init; } = "DIRECT";

    public ProxyNodeConfig? FindNode(string? name) =>
        Nodes.FirstOrDefault(node => node.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public ProxyGroupConfig? FindGroup(string? name) =>
        Groups.FirstOrDefault(group => group.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

internal sealed class ProxyNodeConfig
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string Server { get; init; } = "";
    public int Port { get; init; }
    public string Cipher { get; init; } = "";
    public string Password { get; init; } = "";
    public string Username { get; init; } = "";
    public string Sni { get; init; } = "";
    public string Obfs { get; init; } = "";
    public string ObfsPassword { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string Up { get; init; } = "";
    public string Down { get; init; } = "";
    public bool SkipCertificateValidation { get; init; }
    public bool Supported => Type is "ss" or "trojan" or "http" or "socks5" or "hysteria2" or "hy2";
}

internal sealed class ProxyGroupConfig
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public List<string> Proxies { get; init; } = [];
}
