using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LocalDeck.App.Models;
using YamlDotNet.Serialization;

namespace LocalDeck.App.Services;

public sealed class Hysteria2EngineService : IAsyncDisposable
{
    private static readonly string EnginePath =
        Path.Combine(AppContext.BaseDirectory, "hysteria.exe");
    private static readonly Regex NumericBandwidth =
        new(@"^\d+(?:\.\d+)?$", RegexOptions.Compiled);
    private readonly object _sync = new();
    private readonly Dictionary<string, EngineInstance> _instances =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly WindowsJob _job = new();
    private bool _disposed;

    public static bool IsEngineAvailable => File.Exists(EnginePath);

    internal async Task<int> GetOrStartAsync(
        ProxyNodeConfig node, CancellationToken cancellationToken)
    {
        if (!IsEngineAvailable)
        {
            throw new InvalidOperationException("LocalDeck 内置的 hysteria.exe 不存在。");
        }
        if (string.IsNullOrWhiteSpace(node.Server) || node.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Hysteria2 节点缺少有效的服务器地址或端口。");
        }

        var signature = Signature(node);
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            EngineInstance? stale = null;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_instances.TryGetValue(signature, out var existing))
                {
                    if (!existing.Process.HasExited)
                    {
                        return existing.Port;
                    }
                    stale = existing;
                    _instances.Remove(signature);
                }
            }
            stale?.Dispose();

            var instance = await StartInstanceAsync(node, signature, cancellationToken);
            lock (_sync)
            {
                if (_disposed)
                {
                    instance.Dispose();
                    throw new ObjectDisposedException(nameof(Hysteria2EngineService));
                }
                _instances[signature] = instance;
            }
            return instance.Port;
        }
        finally
        {
            _startGate.Release();
        }
    }

    internal void StopAll()
    {
        EngineInstance[] instances;
        lock (_sync)
        {
            instances = _instances.Values.ToArray();
            _instances.Clear();
        }
        foreach (var instance in instances)
        {
            instance.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }
            _disposed = true;
        }
        StopAll();
        _job.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<EngineInstance> StartInstanceAsync(
        ProxyNodeConfig node, string signature, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(AppPaths.DataDirectory, "hysteria");
        Directory.CreateDirectory(directory);
        var port = FindFreePort();
        var configPath = Path.Combine(directory, $"{signature}.yaml");
        File.WriteAllText(configPath, BuildConfig(node, port), new UTF8Encoding(false));

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(EnginePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory
            }
        };
        process.StartInfo.ArgumentList.Add("--disable-update-check");
        process.StartInfo.ArgumentList.Add("-l");
        process.StartInfo.ArgumentList.Add("warn");
        process.StartInfo.ArgumentList.Add("client");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(configPath);

        try
        {
            process.Start();
            _job.Assign(process);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Hysteria2 引擎启动失败，退出码 {process.ExitCode}。");
                }
                if (await CanConnectAsync(port, cancellationToken))
                {
                    AppPaths.Log($"Hysteria2 engine ready for node {node.Name} on 127.0.0.1:{port}.");
                    return new EngineInstance(signature, process, port, configPath);
                }
                await Task.Delay(120, cancellationToken);
            }

            throw new TimeoutException("Hysteria2 引擎没有在超时时间内监听本地 SOCKS5 端口。");
        }
        catch
        {
            TryTerminate(process);
            process.Dispose();
            throw;
        }
    }

    private static string BuildConfig(ProxyNodeConfig node, int localPort)
    {
        var server = FormatServer(node.Server, node.Port);
        var tls = new Dictionary<string, object?>
        {
            ["sni"] = string.IsNullOrWhiteSpace(node.Sni)
                ? node.Server.Trim('[', ']')
                : node.Sni,
            ["insecure"] = node.SkipCertificateValidation
        };
        if (node.Fingerprint.Length == 64 && node.Fingerprint.All(Uri.IsHexDigit))
        {
            tls["pinSHA256"] = node.Fingerprint;
        }

        var config = new Dictionary<string, object?>
        {
            ["server"] = server,
            ["auth"] = string.IsNullOrWhiteSpace(node.Password) ? node.Username : node.Password,
            ["tls"] = tls,
            ["fastOpen"] = true,
            ["lazy"] = true,
            ["socks5"] = new Dictionary<string, object?>
            {
                ["listen"] = $"127.0.0.1:{localPort}",
                ["disableUDP"] = true
            }
        };

        if (!string.IsNullOrWhiteSpace(node.Obfs) &&
            node.Obfs.Equals("salamander", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(node.ObfsPassword))
        {
            config["obfs"] = new Dictionary<string, object?>
            {
                ["type"] = "salamander",
                ["salamander"] = new Dictionary<string, object?>
                {
                    ["password"] = node.ObfsPassword
                }
            };
        }

        var up = NormalizeBandwidth(node.Up);
        var down = NormalizeBandwidth(node.Down);
        if (up is not null || down is not null)
        {
            config["bandwidth"] = new Dictionary<string, object?>
            {
                ["up"] = up ?? "",
                ["down"] = down ?? ""
            };
        }

        var serializer = new SerializerBuilder()
            .DisableAliases()
            .Build();
        return serializer.Serialize(config);
    }

    private static string FormatServer(string server, int port)
    {
        server = server.Trim();
        if (server.StartsWith('[') && server.Contains("]:"))
        {
            return server;
        }
        if (IPAddress.TryParse(server, out var address) &&
            address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return $"[{server}]:{port}";
        }
        return server.Contains(':') ? server : $"{server}:{port}";
    }

    private static string? NormalizeBandwidth(string value)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return NumericBandwidth.IsMatch(value)
            ? $"{value} mbps"
            : value;
    }

    private static string Signature(ProxyNodeConfig node)
    {
        var material = string.Join('\n',
            node.Name,
            node.Type,
            node.Server,
            node.Port.ToString(CultureInfo.InvariantCulture),
            node.Password,
            node.Username,
            node.Sni,
            node.SkipCertificateValidation,
            node.Obfs,
            node.ObfsPassword,
            node.Fingerprint,
            node.Up,
            node.Down);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> CanConnectAsync(int port, CancellationToken token)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
            // Process cleanup is best effort during shutdown.
        }
    }

    private sealed class EngineInstance : IDisposable
    {
        public EngineInstance(string signature, Process process, int port, string configPath)
        {
            Signature = signature;
            Process = process;
            Port = port;
            ConfigPath = configPath;
        }

        public string Signature { get; }
        public Process Process { get; }
        public int Port { get; }
        public string ConfigPath { get; }

        public void Dispose()
        {
            TryTerminate(Process);
            Process.Dispose();
            try
            {
                File.Delete(ConfigPath);
            }
            catch
            {
                // A later start can overwrite a stale local config.
            }
        }
    }

    private sealed class WindowsJob : IDisposable
    {
        private const int ExtendedLimitInformation = 9;
        private const uint LimitKillOnJobClose = 0x2000;
        private readonly IntPtr _handle;

        public WindowsJob()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero)
            {
                return;
            }
            var information = new ExtendedLimitInformationData
            {
                BasicLimitInformation = new BasicLimitInformation
                {
                    LimitFlags = LimitKillOnJobClose
                }
            };
            _ = SetInformationJobObject(
                _handle,
                ExtendedLimitInformation,
                ref information,
                (uint)Marshal.SizeOf<ExtendedLimitInformationData>());
        }

        public void Assign(Process process)
        {
            if (_handle != IntPtr.Zero)
            {
                _ = AssignProcessToJobObject(_handle, process.Handle);
            }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                _ = CloseHandle(_handle);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformationData
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(
            IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job, int infoClass, ref ExtendedLimitInformationData info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(
            IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
