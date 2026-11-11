using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace LocalDeck.App.Services;

public sealed partial class LocalModelService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SettingsService _settings;
    private readonly object _sync = new();
    private CancellationTokenSource? _downloadCancellation;
    private DownloadStatus _downloadStatus = new(false, null, 0, 0, null);
    private Process? _serverProcess;

    public LocalModelService(SettingsService settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<LocalModelInfo> GetModels()
    {
        var root = _settings.Current.LocalModelDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var result = new List<LocalModelInfo>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.gguf", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    var quantization = QuantizationRegex().Match(info.Name).Value;
                    result.Add(new LocalModelInfo(
                        file,
                        Path.GetFileNameWithoutExtension(info.Name),
                        info.Length,
                        info.LastWriteTime,
                        quantization,
                        EstimateTokensPerSecond(info.Name, info.Length),
                        _settings.Current.LocalModelArguments.GetValueOrDefault(file, "")));
                }
                catch
                {
                    // Ignore model files removed during enumeration.
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Local model enumeration failed: {ex.Message}");
        }

        return result.OrderByDescending(item => item.Modified).ToList();
    }

    public LocalModelEngineInfo GetEngineInfo()
    {
        var directory = _settings.Current.LocalModelEngineDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new LocalModelEngineInfo(null, false, false, false);
        }

        var server = File.Exists(Path.Combine(directory, "llama-server.exe"));
        var benchmark = File.Exists(Path.Combine(directory, "llama-bench.exe"));
        return new LocalModelEngineInfo(
            directory,
            server,
            benchmark,
            IsLocalDeckOwned(directory));
    }

    public async Task<LocalModelEngineInfo> AdoptEngineAsync(
        CancellationToken cancellationToken = default)
    {
        var source = _settings.Current.LocalModelEngineDirectory;
        if (string.IsNullOrWhiteSpace(source) ||
            !File.Exists(Path.Combine(source, "llama-server.exe")))
        {
            throw new InvalidOperationException("当前目录中没有 llama-server.exe。");
        }

        source = Path.GetFullPath(source);
        if (IsLocalDeckOwned(source))
        {
            return GetEngineInfo();
        }

        var enginesRoot = Path.Combine(AppPaths.DataDirectory, "engines");
        Directory.CreateDirectory(enginesRoot);
        var target = Path.Combine(enginesRoot, "llama.cpp-current");
        if (!File.Exists(Path.Combine(target, "llama-server.exe")))
        {
            var temporary = Path.Combine(
                enginesRoot, $"llama.cpp-import-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporary);
            try
            {
                await Task.Run(
                    () => CopyDirectory(source, temporary, cancellationToken),
                    cancellationToken);
                if (Directory.Exists(target))
                {
                    target = Path.Combine(
                        enginesRoot, $"llama.cpp-{DateTime.Now:yyyyMMdd-HHmmss}");
                }
                Directory.Move(temporary, target);
            }
            finally
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, recursive: true);
                }
            }
        }

        if (!File.Exists(Path.Combine(target, "llama-server.exe")))
        {
            throw new IOException("引擎复制完成，但没有找到 llama-server.exe。");
        }

        _settings.Current.LocalModelEngineDirectory = target;
        _settings.Update(_settings.Current);
        AppPaths.Log($"Local model engine adopted from {source} to {target}.");
        return GetEngineInfo();
    }

    public DownloadStatus GetDownloadStatus()
    {
        lock (_sync)
        {
            return _downloadStatus;
        }
    }

    public DownloadStatus StartDownload(string url, string fileName)
    {
        lock (_sync)
        {
            if (_downloadStatus.Running)
            {
                return _downloadStatus;
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Model URL must be HTTP or HTTPS.");
        }

        if (string.IsNullOrWhiteSpace(fileName) ||
            Path.GetFileName(fileName) != fileName ||
            !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid GGUF file name.");
        }

        var root = _settings.Current.LocalModelDirectory
            ?? throw new InvalidOperationException("Local model directory is not configured.");
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, fileName);
        var cancellation = new CancellationTokenSource();
        lock (_sync)
        {
            _downloadCancellation = cancellation;
            _downloadStatus = new DownloadStatus(true, fileName, 0, 0, null);
        }
        _ = Task.Run(() => DownloadAsync(uri, target, cancellation.Token), cancellation.Token);
        return GetDownloadStatus();
    }

    public void CancelDownload()
    {
        _downloadCancellation?.Cancel();
    }

    public void SetLaunchArguments(string modelPath, string arguments)
    {
        _settings.Current.LocalModelArguments[modelPath] = arguments;
        _settings.Update(_settings.Current);
    }

    public async Task<string> BenchmarkAsync(LocalModelInfo model, CancellationToken cancellationToken)
    {
        var directory = _settings.Current.LocalModelEngineDirectory;
        var executable = string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.Combine(directory, "llama-bench.exe");
        if (executable is null || !File.Exists(executable))
        {
            return $"引擎目录中没有 llama-bench.exe。当前估算约 {model.EstimatedTokensPerSecond:F1} t/s。";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(
                executable,
                $"-m \"{model.Path}\" -p 64 -n 64 -r 2 -o json")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;
        return process.ExitCode == 0 ? output : error;
    }

    public bool StopServer()
    {
        lock (_sync)
        {
            if (_serverProcess is null || _serverProcess.HasExited)
            {
                return false;
            }
            _serverProcess.Kill(entireProcessTree: true);
            _serverProcess.Dispose();
            _serverProcess = null;
            return true;
        }
    }

    public LocalServerStatus StartServer(LocalModelInfo model)
    {
        lock (_sync)
        {
            if (_serverProcess is { HasExited: false })
            {
                return new LocalServerStatus(true, _serverProcess.Id, _settings.Current.LocalModelPort, null);
            }
        }

        var directory = _settings.Current.LocalModelEngineDirectory;
        var executable = string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.Combine(directory, "llama-server.exe");
        if (executable is null || !File.Exists(executable))
        {
            return new LocalServerStatus(false, null, null, "请先配置 llama-server.exe 引擎目录。");
        }

        var port = Math.Clamp(_settings.Current.LocalModelPort, 1024, 65535);
        var customArguments = _settings.Current.LocalModelArguments.GetValueOrDefault(model.Path, "");
        var process = Process.Start(new ProcessStartInfo(
            executable,
            $"-m \"{model.Path}\" --host 127.0.0.1 --port {port} {customArguments}")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
        {
            return new LocalServerStatus(false, null, port, "模型引擎启动失败。");
        }

        lock (_sync)
        {
            _serverProcess = process;
        }
        return new LocalServerStatus(true, process.Id, port, null);
    }

    public void OpenCloudModel(string id)
    {
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chatgpt"] = "https://chatgpt.com/",
            ["deepseek"] = "https://chat.deepseek.com/",
            ["grok"] = "https://grok.com/",
            ["claude"] = "https://claude.ai/new",
            ["gemini"] = "https://gemini.google.com/app"
        };
        if (!urls.TryGetValue(id, out var url))
        {
            throw new ArgumentException("Unknown cloud model.");
        }

        var chrome = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
        }.FirstOrDefault(File.Exists);
        if (chrome is null)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo(chrome, $"--new-window \"{url}\"") { UseShellExecute = true });
    }

    public void Dispose()
    {
        _downloadCancellation?.Cancel();
        _downloadCancellation?.Dispose();
        StopServer();
        _http.Dispose();
    }

    private async Task DownloadAsync(Uri uri, string targetPath, CancellationToken token)
    {
        var temporary = targetPath + ".part";
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var destination = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            var buffer = new byte[1024 * 1024];
            long received = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, token);
                if (read == 0)
                {
                    break;
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
                received += read;
                lock (_sync)
                {
                    _downloadStatus = _downloadStatus with
                    {
                        ReceivedBytes = received,
                        TotalBytes = total
                    };
                }
            }

            await destination.FlushAsync(token);
            File.Move(temporary, targetPath, true);
            lock (_sync)
            {
                _downloadStatus = _downloadStatus with { Running = false, ReceivedBytes = received };
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                _downloadStatus = _downloadStatus with { Running = false, Error = "下载已取消" };
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _downloadStatus = _downloadStatus with { Running = false, Error = ex.Message };
            }
            AppPaths.Log($"Model download failed: {ex.Message}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private double EstimateTokensPerSecond(string fileName, long sizeBytes)
    {
        var quantFactor = fileName.Contains("Q8", StringComparison.OrdinalIgnoreCase) ? 1.1 :
            fileName.Contains("Q6", StringComparison.OrdinalIgnoreCase) ? 0.85 :
            fileName.Contains("Q5", StringComparison.OrdinalIgnoreCase) ? 0.72 :
            fileName.Contains("Q4", StringComparison.OrdinalIgnoreCase) ? 0.58 :
            fileName.Contains("F16", StringComparison.OrdinalIgnoreCase) ? 2.1 : 0.7;
        var sizeGb = sizeBytes / 1024d / 1024d / 1024d;
        var bytesPerToken = Math.Max(0.2, quantFactor);
        var bandwidth = 448d;
        var estimate = bandwidth * 0.55 / Math.Max(0.5, sizeGb * bytesPerToken);
        return Math.Clamp(estimate, 1, 120);
    }

    private static bool IsLocalDeckOwned(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dataRoot = Path.GetFullPath(AppPaths.DataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullDirectory.Equals(dataRoot, StringComparison.OrdinalIgnoreCase) ||
               fullDirectory.StartsWith(
                   dataRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(
        string source, string target, CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    [GeneratedRegex(@"(?i)(IQ\d|Q\d(?:_K)?(?:_[SML])?|F16|F32)")]
    private static partial Regex QuantizationRegex();
}

public sealed record LocalModelInfo(
    string Path,
    string Name,
    long SizeBytes,
    DateTime Modified,
    string Quantization,
    double EstimatedTokensPerSecond,
    string CustomArguments);

public sealed record DownloadStatus(
    bool Running,
    string? FileName,
    long ReceivedBytes,
    long TotalBytes,
    string? Error)
{
    public double Percent => TotalBytes > 0 ? ReceivedBytes * 100d / TotalBytes : 0;
}

public sealed record LocalServerStatus(
    bool Running,
    int? ProcessId,
    int? Port,
    string? Error);

public sealed record LocalModelEngineInfo(
    string? Directory,
    bool ServerAvailable,
    bool BenchmarkAvailable,
    bool OwnedByLocalDeck);
