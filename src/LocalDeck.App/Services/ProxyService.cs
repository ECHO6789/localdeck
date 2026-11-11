using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using LocalDeck.App.Models;
using Microsoft.Win32;

namespace LocalDeck.App.Services;

public sealed class ProxyService : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly ProxyProfileService _profiles;
    private readonly Hysteria2EngineService _hysteria2 = new();
    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _acceptTask;
    private Process? _watchdog;
    private string? _error;
    private DateTimeOffset? _startedAt;

    public ProxyService(SettingsService settings, ProxyProfileService profiles)
    {
        _settings = settings;
        _profiles = profiles;
    }

    public bool IsRunning => _listener is not null;

    public void ApplySettings()
    {
        var settings = _settings.Current;
        if (!string.IsNullOrWhiteSpace(settings.ProxyProfilePath))
        {
            try
            {
                _profiles.Load(settings.ProxyProfilePath);
                _error = null;
            }
            catch (Exception ex)
            {
                _error = $"代理配置解析失败：{ex.Message}";
                AppPaths.Log($"Proxy profile load failed: {ex.Message}");
            }
        }
    }

    public ProxyStatus GetStatus()
    {
        var settings = _settings.Current;
        return new ProxyStatus(
            IsRunning,
            "127.0.0.1",
            settings.ProxyPort,
            settings.ProxyMode,
            settings.ProxySelectedNode,
            settings.ProxyProfilePath,
            _profiles.LoadedAt,
            _profiles.GetNodeInfos(),
            IsSystemProxyEnabled(),
            _error);
    }

    public async Task<ProxyStatus> StartAsync()
    {
        lock (_gate)
        {
            if (_listener is not null)
            {
                return GetStatus();
            }
        }

        var settings = _settings.Current;
        if (string.IsNullOrWhiteSpace(settings.ProxyProfilePath) ||
            _profiles.Load(settings.ProxyProfilePath) is null)
        {
            throw new InvalidOperationException("请先导入代理 YAML 配置。");
        }

        TcpListener? listener = null;
        var selectedPort = settings.ProxyPort;
        Exception? lastError = null;
        for (var offset = 0; offset < 20 && settings.ProxyPort + offset <= 65535; offset++)
        {
            var candidate = settings.ProxyPort + offset;
            var probe = new TcpListener(IPAddress.Loopback, candidate);
            try
            {
                probe.Start(128);
                listener = probe;
                selectedPort = candidate;
                break;
            }
            catch (SocketException ex)
            {
                lastError = ex;
                probe.Stop();
            }
        }

        if (listener is null)
        {
            _error = $"端口 {settings.ProxyPort} 及后续备用端口无法监听：{lastError?.Message}";
            throw new InvalidOperationException(_error, lastError);
        }
        if (selectedPort != settings.ProxyPort)
        {
            settings.ProxyPort = selectedPort;
            _settings.Update(settings);
            AppPaths.Log($"Proxy preferred port was busy; using 127.0.0.1:{selectedPort}.");
        }

        var stop = new CancellationTokenSource();
        lock (_gate)
        {
            _listener = listener;
            _stop = stop;
            _error = null;
            _startedAt = DateTimeOffset.Now;
            _acceptTask = AcceptLoopAsync(listener, stop.Token);
        }

        return GetStatus();
    }

    public async Task<ProxyStatus> StopAsync(bool disableSystemProxy = true)
    {
        var wasRunning = IsRunning;
        CancellationTokenSource? stop;
        TcpListener? listener;
        Task? acceptTask;
        lock (_gate)
        {
            stop = _stop;
            listener = _listener;
            acceptTask = _acceptTask;
            _listener = null;
            _stop = null;
            _acceptTask = null;
            _startedAt = null;
        }

        stop?.Cancel();
        listener?.Stop();
        if (acceptTask is not null)
        {
            try
            {
                await acceptTask;
            }
            catch
            {
                // Listener shutdown is expected to cancel the accept loop.
            }
        }

        _hysteria2.StopAll();
        stop?.Dispose();
        if (disableSystemProxy &&
            (wasRunning || _settings.Current.ProxySystemProxyEnabled))
        {
            SetSystemProxy(false);
            if (_settings.Current.ProxySystemProxyEnabled)
            {
                _settings.Current.ProxySystemProxyEnabled = false;
                _settings.Update(_settings.Current);
            }
        }

        return GetStatus();
    }

    public bool SetSystemProxy(bool enabled)
    {
        var port = _settings.Current.ProxyPort;
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true)
            ?? throw new InvalidOperationException("Internet Settings registry key is unavailable.");

        key.SetValue("ProxyEnable", enabled ? 1 : 0, RegistryValueKind.DWord);
        if (enabled)
        {
            key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
            key.SetValue(
                "ProxyOverride",
                "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>",
                RegistryValueKind.String);
            EnsureWatchdog(port);
        }
        else
        {
            StopWatchdog();
        }

        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        return enabled;
    }

    public bool DisableOwnedSystemProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key is null)
        {
            return false;
        }

        var enabled = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0) != 0;
        var server = key.GetValue("ProxyServer")?.ToString() ?? "";
        var ownedEndpoint = $"127.0.0.1:{_settings.Current.ProxyPort}";
        if (!enabled || !server.Equals(ownedEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        SetSystemProxy(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(disableSystemProxy: true);
        await _hysteria2.DisposeAsync();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = HandleClientAsync(client, token);
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            AppPaths.Log($"Proxy accept loop stopped: {ex}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            // This timeout covers the entire tunnel, so it must allow normal
            // downloads and long-lived media streams rather than only handshakes.
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                var first = new byte[1];
                await stream.ReadExactlyAsync(first, timeout.Token);
                if (first[0] == 0x05)
                {
                    await HandleSocks5Async(stream, timeout.Token);
                }
                else
                {
                    await HandleHttpAsync(stream, first[0], timeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Client timeouts are normal for scanners and abandoned browser requests.
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Proxy client failed: {ex.Message}");
            }
        }
    }

    private async Task HandleSocks5Async(NetworkStream client, CancellationToken token)
    {
        var count = new byte[1];
        await client.ReadExactlyAsync(count, token);
        var methods = new byte[count[0]];
        await client.ReadExactlyAsync(methods, token);
        if (!methods.Contains((byte)0))
        {
            await client.WriteAsync(new byte[] { 0x05, 0xFF }, token);
            return;
        }

        await client.WriteAsync(new byte[] { 0x05, 0x00 }, token);
        var request = new byte[4];
        await client.ReadExactlyAsync(request, token);
        if (request[0] != 0x05 || request[1] != 0x01)
        {
            await WriteSocksFailureAsync(client, 0x07, token);
            return;
        }

        var host = await ReadSocksAddressAsync(client, request[3], token);
        var portBytes = new byte[2];
        await client.ReadExactlyAsync(portBytes, token);
        var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);

        Tunnel? tunnel = null;
        try
        {
            tunnel = await OpenTunnelAsync(host, port, token);
            await client.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, token);
            await PumpAsync(client, tunnel.Stream, token);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"SOCKS5 proxy request failed for {host}:{port}: {ex}");
            if (tunnel is null)
            {
                await WriteSocksFailureAsync(client, 0x05, token);
            }
        }
        finally
        {
            if (tunnel is not null)
            {
                await tunnel.DisposeAsync();
            }
        }
    }

    private async Task HandleHttpAsync(NetworkStream client, byte firstByte, CancellationToken token)
    {
        var header = await ReadHttpHeaderAsync(client, firstByte, token);
        var text = Encoding.ASCII.GetString(header);
        var firstLineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        var firstLine = firstLineEnd < 0 ? text : text[..firstLineEnd];
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            await WriteAsciiAsync(client, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n", token);
            return;
        }

        var method = parts[0];
        var target = parts[1];
        string host;
        int port;
        byte[] outboundHeader;
        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            (host, port) = ParseHostPort(target, 443);
            outboundHeader = [];
        }
        else
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                await WriteAsciiAsync(client, "HTTP/1.1 400 Absolute URL Required\r\nConnection: close\r\n\r\n", token);
                return;
            }

            host = uri.IdnHost;
            port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
            var normalizedTarget = uri.PathAndQuery;
            var normalizedFirstLine = $"{method} {normalizedTarget} {parts[2]}";
            var rest = firstLineEnd < 0 ? "" : text[(firstLineEnd + 2)..];
            outboundHeader = Encoding.ASCII.GetBytes(normalizedFirstLine + "\r\n" + rest);
        }

        Tunnel? tunnel = null;
        try
        {
            tunnel = await OpenTunnelAsync(host, port, token);
            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAsciiAsync(client, "HTTP/1.1 200 Connection Established\r\nProxy-Agent: LocalDeck\r\n\r\n", token);
            }
            else
            {
                await tunnel.Stream.WriteAsync(outboundHeader, token);
                await tunnel.Stream.FlushAsync(token);
            }

            await PumpAsync(client, tunnel.Stream, token);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"HTTP proxy request failed for {host}:{port}: {ex}");
            if (tunnel is null)
            {
                await WriteAsciiAsync(client, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n", token);
            }
        }
        finally
        {
            if (tunnel is not null)
            {
                await tunnel.DisposeAsync();
            }
        }
    }

    private async Task<Tunnel> OpenTunnelAsync(string host, int port, CancellationToken token)
    {
        var node = ResolveNode(host, out var policy);
        if (policy.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
            policy.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Policy rejected {host}.");
        }

        if (node is null)
        {
            return await OpenDirectAsync(host, port, token);
        }

        return node.Type switch
        {
            "ss" => await OpenShadowsocksAsync(node, host, port, token),
            "trojan" => await OpenTrojanAsync(node, host, port, token),
            "hysteria2" or "hy2" => await OpenHysteria2Async(node, host, port, token),
            "http" => await OpenHttpUpstreamAsync(node, host, port, token),
            "socks5" => await OpenSocksUpstreamAsync(node, host, port, token),
            _ => throw new NotSupportedException($"Protocol {node.Type} is not supported by the built-in core.")
        };
    }

    private ProxyNodeConfig? ResolveNode(string host, out string policy)
    {
        var settings = _settings.Current;
        var profile = _profiles.Current ?? throw new InvalidOperationException("代理配置尚未加载。");
        if (settings.ProxyMode.Equals("direct", StringComparison.OrdinalIgnoreCase))
        {
            policy = "DIRECT";
            return null;
        }

        if (settings.ProxyMode.Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            policy = settings.ProxySelectedNode ?? "DIRECT";
            return ResolvePolicy(profile, policy);
        }

        foreach (var rule in profile.Rules)
        {
            var parts = rule.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var type = parts[0].ToUpperInvariant();
            if (type is "MATCH" or "FINAL")
            {
                policy = parts.Length > 1 ? parts[1] : profile.FinalPolicy;
                return ResolvePolicy(profile, policy);
            }

            if (parts.Length < 3)
            {
                continue;
            }

            var value = parts[1];
            var matched = type switch
            {
                "DOMAIN" => host.Equals(value, StringComparison.OrdinalIgnoreCase),
                "DOMAIN-SUFFIX" => host.EndsWith("." + value, StringComparison.OrdinalIgnoreCase) ||
                                   host.Equals(value, StringComparison.OrdinalIgnoreCase),
                "DOMAIN-KEYWORD" => host.Contains(value, StringComparison.OrdinalIgnoreCase),
                "IP-CIDR" or "IP-CIDR6" => CIDRContains(value, host),
                _ => false
            };
            if (matched)
            {
                policy = parts[2];
                return ResolvePolicy(profile, policy);
            }
        }

        policy = profile.FinalPolicy;
        return ResolvePolicy(profile, policy);
    }

    private ProxyNodeConfig? ResolvePolicy(ProxyProfile profile, string policy)
    {
        if (policy.Equals("DIRECT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var node = profile.FindNode(policy);
        if (node is not null)
        {
            return node;
        }

        var group = profile.FindGroup(policy);
        if (group is not null)
        {
            var selected = _settings.Current.ProxySelectedNode;
            if (!string.IsNullOrWhiteSpace(selected) &&
                group.Proxies.Contains(selected, StringComparer.OrdinalIgnoreCase))
            {
                if (selected.Equals("DIRECT", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                var selectedNode = profile.FindNode(selected);
                if (selectedNode is not null)
                {
                    return selectedNode;
                }
                var selectedGroup = ResolveGroupReference(profile, selected, depth: 0);
                if (selectedGroup is not null)
                {
                    return selectedGroup;
                }
            }
            foreach (var candidate in group.Proxies)
            {
                if (candidate.Equals("DIRECT", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                if (candidate.Equals("REJECT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var groupNode = profile.FindNode(candidate) ??
                                ResolveGroupReference(profile, candidate, depth: 0);
                if (groupNode?.Supported == true)
                {
                    return groupNode;
                }
            }
        }

        throw new InvalidOperationException($"代理节点“{policy}”没有受支持的上游协议。");
    }

    private static ProxyNodeConfig? ResolveGroupReference(ProxyProfile profile, string policy, int depth)
    {
        if (depth > 4)
        {
            return null;
        }

        var group = profile.FindGroup(policy);
        if (group is null)
        {
            return null;
        }

        foreach (var child in group.Proxies)
        {
            var node = profile.FindNode(child);
            if (node?.Supported == true)
            {
                return node;
            }

            var nested = ResolveGroupReference(profile, child, depth + 1);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool CIDRContains(string cidr, string host)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) ||
            !int.TryParse(parts[1], out var prefix))
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length ||
            prefix < 0 ||
            prefix > addressBytes.Length * 8)
        {
            return false;
        }

        for (var i = 0; i < addressBytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            if (bits == 0)
            {
                break;
            }
            var mask = (byte)(0xFF << (8 - bits));
            if ((addressBytes[i] & mask) != (networkBytes[i] & mask))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<Tunnel> OpenDirectAsync(
        string host, int port, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, token);
        return new Tunnel(client, client.GetStream());
    }

    private static async Task<Tunnel> OpenHttpUpstreamAsync(
        ProxyNodeConfig node, string host, int port, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(node.Server, node.Port, token);
        Stream stream = client.GetStream();
        if (node.Sni.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = node.Sni.Replace("https://", "", StringComparison.OrdinalIgnoreCase),
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, token);
            stream = ssl;
        }

        var request = new StringBuilder()
            .Append($"CONNECT {FormatHost(host)}:{port} HTTP/1.1\r\n")
            .Append($"Host: {FormatHost(host)}:{port}\r\n");
        if (!string.IsNullOrWhiteSpace(node.Username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{node.Username}:{node.Password}"));
            request.Append("Proxy-Authorization: Basic ").Append(credentials).Append("\r\n");
        }
        request.Append("Connection: keep-alive\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), token);
        var response = await ReadHttpHeaderAsync(stream, null, token);
        var statusLine = Encoding.ASCII.GetString(response).Split("\r\n")[0];
        if (!statusLine.Contains(" 200 ", StringComparison.Ordinal))
        {
            client.Dispose();
            throw new IOException($"HTTP upstream rejected CONNECT: {statusLine}");
        }

        return new Tunnel(client, stream);
    }

    private static async Task<Tunnel> OpenSocksUpstreamAsync(
        ProxyNodeConfig node, string host, int port, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(node.Server, node.Port, token);
        var stream = client.GetStream();
        var needAuth = !string.IsNullOrWhiteSpace(node.Username);
        await stream.WriteAsync(needAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, token);
        var method = new byte[2];
        await stream.ReadExactlyAsync(method, token);
        if (method[1] == 0x02)
        {
            var user = Encoding.UTF8.GetBytes(node.Username);
            var password = Encoding.UTF8.GetBytes(node.Password);
            var auth = new byte[3 + user.Length + password.Length];
            auth[0] = 1;
            auth[1] = (byte)user.Length;
            user.CopyTo(auth, 2);
            auth[2 + user.Length] = (byte)password.Length;
            password.CopyTo(auth, 3 + user.Length);
            await stream.WriteAsync(auth, token);
            var authReply = new byte[2];
            await stream.ReadExactlyAsync(authReply, token);
            if (authReply[1] != 0)
            {
                client.Dispose();
                throw new IOException("SOCKS5 upstream authentication failed.");
            }
        }
        else if (method[1] != 0)
        {
            client.Dispose();
            throw new IOException("SOCKS5 upstream has no supported authentication method.");
        }

        var address = BuildSocksAddress(host, port);
        var request = new byte[3 + address.Length];
        request[0] = 5;
        request[1] = 1;
        address.CopyTo(request, 3);
        await stream.WriteAsync(request, token);
        var response = new byte[4];
        await stream.ReadExactlyAsync(response, token);
        if (response[1] != 0)
        {
            client.Dispose();
            throw new IOException($"SOCKS5 upstream connect failed with code {response[1]}.");
        }
        var addressLength = response[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadLengthPrefixAsync(stream, token),
            _ => 0
        };
        if (addressLength > 0)
        {
            var remainingAddress = new byte[addressLength];
            await stream.ReadExactlyAsync(remainingAddress, token);
        }
        var ignoredPort = new byte[2];
        await stream.ReadExactlyAsync(ignoredPort, token);
        return new Tunnel(client, stream);
    }

    private async Task<Tunnel> OpenHysteria2Async(
        ProxyNodeConfig node, string host, int port, CancellationToken token)
    {
        var localPort = await _hysteria2.GetOrStartAsync(node, token);
        var localNode = new ProxyNodeConfig
        {
            Name = $"{node.Name} / LocalDeck Hysteria2",
            Type = "socks5",
            Server = "127.0.0.1",
            Port = localPort
        };
        return await OpenSocksUpstreamAsync(localNode, host, port, token);
    }

    private static async Task<Tunnel> OpenTrojanAsync(
        ProxyNodeConfig node, string host, int port, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(node.Server, node.Port, token);
        var network = client.GetStream();
        var ssl = new SslStream(network, leaveInnerStreamOpen: false, (_, _, _, errors) =>
            node.SkipCertificateValidation || errors == SslPolicyErrors.None);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = string.IsNullOrWhiteSpace(node.Sni) ? node.Server : node.Sni,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, token);

        var passwordHash = Convert.ToHexString(Sha224.HashData(Encoding.UTF8.GetBytes(node.Password)))
            .ToLowerInvariant();
        var header = new List<byte>();
        header.AddRange(Encoding.ASCII.GetBytes(passwordHash));
        header.AddRange("\r\n"u8.ToArray());
        header.Add(0x01);
        header.AddRange(BuildSocksAddress(host, port));
        header.AddRange("\r\n"u8.ToArray());
        await ssl.WriteAsync(header.ToArray(), token);
        await ssl.FlushAsync(token);
        return new Tunnel(client, ssl);
    }

    private static async Task<Tunnel> OpenShadowsocksAsync(
        ProxyNodeConfig node, string host, int port, CancellationToken token)
    {
        var keyLength = node.Cipher.ToLowerInvariant() switch
        {
            "aes-128-gcm" => 16,
            "aes-192-gcm" => 24,
            "aes-256-gcm" => 32,
            "chacha20-ietf-poly1305" => 32,
            _ => 0
        };
        if (keyLength == 0)
        {
            throw new NotSupportedException($"Shadowsocks cipher {node.Cipher} is not supported.");
        }

        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(node.Server, node.Port, token);
        var network = client.GetStream();
        var masterKey = EvpBytesToKey(node.Password, keyLength);
        var stream = new ShadowsocksStream(network, masterKey, node.Cipher.ToLowerInvariant());
        await stream.InitializeAsync(BuildSocksAddress(host, port), token);
        return new Tunnel(client, stream);
    }

    private static async Task PumpAsync(Stream left, Stream right, CancellationToken token)
    {
        using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var leftToRight = CopyAsync(left, right, pumpStop.Token);
        var rightToLeft = CopyAsync(right, left, pumpStop.Token);
        await Task.WhenAny(leftToRight, rightToLeft);
        pumpStop.Cancel();
        try
        {
            await Task.WhenAll(leftToRight, rightToLeft);
        }
        catch
        {
            // One side closing tears down both directions.
        }
    }

    private static async Task CopyAsync(Stream source, Stream target, CancellationToken token)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, token);
            if (read == 0)
            {
                break;
            }
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            await target.FlushAsync(token);
        }
    }

    private static async Task<byte[]> ReadHttpHeaderAsync(
        Stream stream, byte? firstByte, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        if (firstByte is not null)
        {
            buffer.WriteByte(firstByte.Value);
        }

        var one = new byte[1];
        var matched = 0;
        while (buffer.Length < 64 * 1024)
        {
            await stream.ReadExactlyAsync(one, token);
            buffer.WriteByte(one[0]);
            matched = (matched, one[0]) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0
            };
            if (matched == 4)
            {
                return buffer.ToArray();
            }
        }

        throw new InvalidDataException("HTTP header too large.");
    }

    private static async Task<string> ReadSocksAddressAsync(
        Stream stream, byte addressType, CancellationToken token)
    {
        if (addressType == 1)
        {
            var bytes = new byte[4];
            await stream.ReadExactlyAsync(bytes, token);
            return new IPAddress(bytes).ToString();
        }
        if (addressType == 4)
        {
            var bytes = new byte[16];
            await stream.ReadExactlyAsync(bytes, token);
            return new IPAddress(bytes).ToString();
        }
        if (addressType == 3)
        {
            var bytes = await ReadLengthPrefixedAsync(stream, token);
            return Encoding.UTF8.GetString(bytes);
        }

        throw new InvalidDataException("Unsupported SOCKS5 address type.");
    }

    private static async Task<byte[]> ReadLengthPrefixedAsync(Stream stream, CancellationToken token)
    {
        var length = await ReadLengthPrefixAsync(stream, token);
        var value = new byte[length];
        await stream.ReadExactlyAsync(value, token);
        return value;
    }

    private static async Task<int> ReadLengthPrefixAsync(Stream stream, CancellationToken token)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, token);
        return length[0];
    }

    private static byte[] BuildSocksAddress(string host, int port)
    {
        var result = new List<byte>();
        if (IPAddress.TryParse(host, out var address))
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                result.Add(1);
                result.AddRange(address.GetAddressBytes());
            }
            else
            {
                result.Add(4);
                result.AddRange(address.GetAddressBytes());
            }
        }
        else
        {
            var hostBytes = Encoding.UTF8.GetBytes(host);
            result.Add(3);
            result.Add((byte)hostBytes.Length);
            result.AddRange(hostBytes);
        }

        Span<byte> portBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        result.AddRange(portBytes.ToArray());
        return result.ToArray();
    }

    private static (string Host, int Port) ParseHostPort(string value, int defaultPort)
    {
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var end = value.IndexOf(']');
            if (end > 0)
            {
                var host = value[1..end];
                var port = end + 2 < value.Length && int.TryParse(value[(end + 2)..], out var parsed)
                    ? parsed
                    : defaultPort;
                return (host, port);
            }
        }

        var separator = value.LastIndexOf(':');
        return separator > 0 && int.TryParse(value[(separator + 1)..], out var portNumber)
            ? (value[..separator], portNumber)
            : (value, defaultPort);
    }

    private static string FormatHost(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;

    private static byte[] EvpBytesToKey(string password, int keyLength)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        using var output = new MemoryStream();
        var previous = Array.Empty<byte>();
        while (output.Length < keyLength)
        {
            var input = new byte[previous.Length + passwordBytes.Length];
            previous.CopyTo(input, 0);
            passwordBytes.CopyTo(input, previous.Length);
            previous = MD5.HashData(input);
            output.Write(previous);
        }
        return output.ToArray()[..keyLength];
    }

    private static class Sha224
    {
        private static readonly uint[] RoundConstants =
        [
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5,
            0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
            0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
            0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
            0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
            0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3,
            0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5,
            0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
            0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
        ];

        public static byte[] HashData(byte[] data)
        {
            var messageLength = data.Length;
            var paddedLength = ((messageLength + 8) / 64 + 1) * 64;
            var padded = new byte[paddedLength];
            data.CopyTo(padded, 0);
            padded[messageLength] = 0x80;
            var bitLength = (ulong)messageLength * 8;
            BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(paddedLength - 8), bitLength);

            uint[] state =
            [
                0xc1059ed8, 0x367cd507, 0x3070dd17, 0xf70e5939,
                0xffc00b31, 0x68581511, 0x64f98fa7, 0xbefa4fa4
            ];
            Span<uint> schedule = stackalloc uint[64];

            for (var offset = 0; offset < padded.Length; offset += 64)
            {
                for (var i = 0; i < 16; i++)
                {
                    schedule[i] = BinaryPrimitives.ReadUInt32BigEndian(
                        padded.AsSpan(offset + i * 4, 4));
                }
                for (var i = 16; i < 64; i++)
                {
                    var s0 = RotateRight(schedule[i - 15], 7) ^
                             RotateRight(schedule[i - 15], 18) ^
                             (schedule[i - 15] >> 3);
                    var s1 = RotateRight(schedule[i - 2], 17) ^
                             RotateRight(schedule[i - 2], 19) ^
                             (schedule[i - 2] >> 10);
                    schedule[i] = unchecked(schedule[i - 16] + s0 + schedule[i - 7] + s1);
                }

                var a = state[0];
                var b = state[1];
                var c = state[2];
                var d = state[3];
                var e = state[4];
                var f = state[5];
                var g = state[6];
                var h = state[7];
                for (var i = 0; i < 64; i++)
                {
                    var s1 = RotateRight(e, 6) ^ RotateRight(e, 11) ^ RotateRight(e, 25);
                    var choice = (e & f) ^ (~e & g);
                    var temp1 = unchecked(h + s1 + choice + RoundConstants[i] + schedule[i]);
                    var s0 = RotateRight(a, 2) ^ RotateRight(a, 13) ^ RotateRight(a, 22);
                    var majority = (a & b) ^ (a & c) ^ (b & c);
                    var temp2 = unchecked(s0 + majority);
                    h = g;
                    g = f;
                    f = e;
                    e = unchecked(d + temp1);
                    d = c;
                    c = b;
                    b = a;
                    a = unchecked(temp1 + temp2);
                }

                state[0] = unchecked(state[0] + a);
                state[1] = unchecked(state[1] + b);
                state[2] = unchecked(state[2] + c);
                state[3] = unchecked(state[3] + d);
                state[4] = unchecked(state[4] + e);
                state[5] = unchecked(state[5] + f);
                state[6] = unchecked(state[6] + g);
                state[7] = unchecked(state[7] + h);
            }

            var result = new byte[28];
            for (var i = 0; i < 7; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4, 4), state[i]);
            }
            return result;
        }

        private static uint RotateRight(uint value, int count) =>
            (value >> count) | (value << (32 - count));
    }

    private static async Task WriteSocksFailureAsync(
        Stream stream, byte code, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
    }

    private static async Task WriteAsciiAsync(
        Stream stream, string value, CancellationToken token)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(value), token);
    }

    private static bool IsSystemProxyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) != 0;
    }

    private void EnsureWatchdog(int port)
    {
        if (_watchdog is { HasExited: false })
        {
            return;
        }
        _watchdog?.Dispose();
        _watchdog = ProxyWatchdogService.Start(Environment.ProcessId, port);
        if (_watchdog is null)
        {
            AppPaths.Log("Proxy watchdog could not be started.");
        }
    }

    private void StopWatchdog()
    {
        try
        {
            if (_watchdog is { HasExited: false })
            {
                _watchdog.Kill(entireProcessTree: true);
                _watchdog.WaitForExit(1000);
            }
        }
        catch
        {
            // The watchdog exits by itself when the parent process ends.
        }
        _watchdog?.Dispose();
        _watchdog = null;
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr internet, int option, IntPtr buffer, int bufferLength);

    private sealed class Tunnel : IAsyncDisposable
    {
        private readonly TcpClient _client;

        public Tunnel(TcpClient client, Stream stream)
        {
            _client = client;
            Stream = stream;
        }

        public Stream Stream { get; }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            _client.Dispose();
        }
    }

    private sealed class ShadowsocksStream : Stream
    {
        private readonly NetworkStream _inner;
        private readonly byte[] _masterKey;
        private readonly string _cipher;
        private byte[] _encryptNonce = new byte[12];
        private byte[] _decryptNonce = new byte[12];
        private readonly SemaphoreSlim _encryptLock = new(1, 1);
        private readonly SemaphoreSlim _decryptLock = new(1, 1);
        private byte[] _decrypted = [];
        private int _decryptedOffset;
        private byte[]? _encryptSubKey;
        private byte[]? _decryptSubKey;
        private bool _readSalt;

        public ShadowsocksStream(NetworkStream inner, byte[] masterKey, string cipher)
        {
            _inner = inner;
            _masterKey = masterKey;
            _cipher = cipher;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public async Task InitializeAsync(byte[] targetAddress, CancellationToken token)
        {
            var salt = RandomNumberGenerator.GetBytes(_masterKey.Length);
            _encryptSubKey = HkdfSha1(_masterKey, salt, "ss-subkey"u8.ToArray(), _masterKey.Length);
            await _inner.WriteAsync(salt, token);
            await WriteChunkAsync(targetAddress, token);
            await _inner.FlushAsync(token);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            if (_decryptedOffset >= _decrypted.Length)
            {
                await _decryptLock.WaitAsync(cancellationToken);
                try
                {
                    if (_decryptedOffset >= _decrypted.Length)
                    {
                        await FillDecryptedAsync(cancellationToken);
                    }
                }
                finally
                {
                    _decryptLock.Release();
                }
            }

            var available = Math.Min(buffer.Length, _decrypted.Length - _decryptedOffset);
            _decrypted.AsMemory(_decryptedOffset, available).CopyTo(buffer);
            _decryptedOffset += available;
            return available;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _encryptLock.WaitAsync(cancellationToken);
            try
            {
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var size = Math.Min(0x3FFF, buffer.Length - offset);
                    await WriteChunkAsync(buffer.Slice(offset, size).ToArray(), cancellationToken);
                    offset += size;
                }
                await _inner.FlushAsync(cancellationToken);
            }
            finally
            {
                _encryptLock.Release();
            }
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _encryptLock.Dispose();
                _decryptLock.Dispose();
            }
            base.Dispose(disposing);
        }

        private async Task FillDecryptedAsync(CancellationToken token)
        {
            if (!_readSalt)
            {
                var remoteSalt = new byte[_masterKey.Length];
                await _inner.ReadExactlyAsync(remoteSalt, token);
                _decryptSubKey = HkdfSha1(_masterKey, remoteSalt, "ss-subkey"u8.ToArray(), _masterKey.Length);
                _readSalt = true;
            }

            var encryptedLength = new byte[2 + 16];
            await _inner.ReadExactlyAsync(encryptedLength, token);
            var lengthPlain = Decrypt(encryptedLength, _decryptSubKey!, ref _decryptNonce, 2);
            var length = BinaryPrimitives.ReadUInt16BigEndian(lengthPlain);
            if (length is 0 or > 0x3FFF)
            {
                throw new InvalidDataException("Invalid Shadowsocks chunk length.");
            }

            var encryptedPayload = new byte[length + 16];
            await _inner.ReadExactlyAsync(encryptedPayload, token);
            _decrypted = Decrypt(encryptedPayload, _decryptSubKey!, ref _decryptNonce, length);
            _decryptedOffset = 0;
        }

        private async Task WriteChunkAsync(byte[] payload, CancellationToken token)
        {
            if (_encryptSubKey is null)
            {
                throw new InvalidOperationException("Shadowsocks stream is not initialized.");
            }

            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)payload.Length);
            var encryptedLength = Encrypt(length, _encryptSubKey, ref _encryptNonce, length.Length);
            var encryptedPayload = Encrypt(payload, _encryptSubKey, ref _encryptNonce, payload.Length);
            await _inner.WriteAsync(encryptedLength, token);
            await _inner.WriteAsync(encryptedPayload, token);
        }

        private byte[] Encrypt(byte[] plain, byte[] key, ref byte[] nonce, int plainLength)
        {
            var cipher = new byte[plainLength];
            var tag = new byte[16];
            if (_cipher == "chacha20-ietf-poly1305")
            {
                using var aead = new ChaCha20Poly1305(key);
                aead.Encrypt(nonce, plain.AsSpan(0, plainLength), cipher, tag);
            }
            else
            {
                using var aead = new AesGcm(key, 16);
                aead.Encrypt(nonce, plain.AsSpan(0, plainLength), cipher, tag);
            }
            IncrementNonce(nonce);
            return cipher.Concat(tag).ToArray();
        }

        private byte[] Decrypt(byte[] encrypted, byte[] key, ref byte[] nonce, int plainLength)
        {
            var plain = new byte[plainLength];
            var cipher = encrypted.AsSpan(0, plainLength);
            var tag = encrypted.AsSpan(plainLength, 16);
            if (_cipher == "chacha20-ietf-poly1305")
            {
                using var aead = new ChaCha20Poly1305(key);
                aead.Decrypt(nonce, cipher, tag, plain);
            }
            else
            {
                using var aead = new AesGcm(key, 16);
                aead.Decrypt(nonce, cipher, tag, plain);
            }
            IncrementNonce(nonce);
            return plain;
        }

        private static void IncrementNonce(byte[] nonce)
        {
            for (var i = 0; i < nonce.Length; i++)
            {
                nonce[i]++;
                if (nonce[i] != 0)
                {
                    break;
                }
            }
        }

        private static byte[] HkdfSha1(byte[] ikm, byte[] salt, byte[] info, int length)
        {
            using var hmac = new HMACSHA1(salt);
            var pseudoRandomKey = hmac.ComputeHash(ikm);
            using var expand = new HMACSHA1(pseudoRandomKey);
            using var output = new MemoryStream();
            var previous = Array.Empty<byte>();
            for (byte counter = 1; output.Length < length; counter++)
            {
                var input = new byte[previous.Length + info.Length + 1];
                previous.CopyTo(input, 0);
                info.CopyTo(input, previous.Length);
                input[^1] = counter;
                previous = expand.ComputeHash(input);
                output.Write(previous);
            }
            return output.ToArray()[..length];
        }
    }
}
