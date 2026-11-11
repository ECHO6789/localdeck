using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace LocalDeck.App.Services;

public sealed class DlssUnlockedService : IDisposable
{
    public const string Version = "NR-v0.9.86";
    public const string DownloadUrl =
        "https://github.com/ShyVortex/dlss-unlocked/releases/download/NR-v0.9.86/dlss-unlocked-standalone-NR-v0.9.86.zip";
    public const string UpstreamUrl = "https://github.com/ShyVortex/dlss-unlocked";
    public const string PackageSha256 =
        "78436B3B13EA83BED88BFC54C01A00A9B19A6247C9A0BF3D556EBC0A542360FA";
    public const long PackageBytes = 629_540_064;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<int?> _proxyPort;
    private readonly object _sync = new();
    private readonly string _packageDirectory;
    private readonly string _backupDirectory;
    private readonly string _installationFile;
    private CancellationTokenSource? _cancellation;
    private DownloadStatus _download = new(false, Path.GetFileName(DownloadUrl), 0, PackageBytes, null);
    private bool _busy;
    private string _operation = "";
    private string? _error;
    private string? _verifiedPackagePath;
    private string? _invalidPackagePath;

    public DlssUnlockedService(Func<int?> proxyPort)
    {
        _proxyPort = proxyPort;
        var root = Path.Combine(AppPaths.DataDirectory, "dlss-unlocked");
        _packageDirectory = Path.Combine(root, "packages");
        _backupDirectory = Path.Combine(root, "backups");
        _installationFile = Path.Combine(root, "installations.json");
        Directory.CreateDirectory(_packageDirectory);
        Directory.CreateDirectory(_backupDirectory);
    }

    public DlssUnlockedStatus GetStatus()
    {
        var packagePath = Path.Combine(_packageDirectory, Path.GetFileName(DownloadUrl));
        var packageReady = File.Exists(packagePath) &&
                           new FileInfo(packagePath).Length == PackageBytes &&
                           IsCachedPackageVerified(packagePath);
        lock (_sync)
        {
            return new DlssUnlockedStatus(
                Version,
                DownloadUrl,
                PackageSha256,
                packagePath,
                packageReady,
                _download,
                _busy,
                _operation,
                _error,
                IsGpuSupported(),
                ReadInstallations());
        }
    }

    public DlssUnlockedStatus StartDownload()
    {
        lock (_sync)
        {
            if (_download.Running || _busy)
            {
                return GetStatus();
            }
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _download = new DownloadStatus(
                true, Path.GetFileName(DownloadUrl), 0, PackageBytes, null);
            _error = null;
            _invalidPackagePath = null;
        }
        var token = _cancellation.Token;
        _ = Task.Run(() => DownloadPackageAsync(token), token);
        return GetStatus();
    }

    public void Cancel()
    {
        _cancellation?.Cancel();
    }

    public DlssUnlockedStatus StartInstall(string gameDirectory, string proxyName)
    {
        var gameRoot = ValidateGameDirectory(gameDirectory);
        if (proxyName is not ("dxgi" or "version"))
        {
            throw new ArgumentException("代理文件名必须是 dxgi 或 version。");
        }
        var packagePath = Path.Combine(_packageDirectory, Path.GetFileName(DownloadUrl));
        if (!File.Exists(packagePath) || new FileInfo(packagePath).Length != PackageBytes)
        {
            throw new InvalidOperationException("DLSS Unlocked 安装包尚未下载。");
        }
        lock (_sync)
        {
            if (_busy)
            {
                return GetStatus();
            }
            if (ReadInstallations().Any(item =>
                    item.GamePath.Equals(gameRoot, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("这个游戏目录已经由 LocalDeck 安装过。");
            }
            _busy = true;
            _operation = "正在解压并备份游戏文件…";
            _error = null;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
        }
        var token = _cancellation.Token;
        _ = Task.Run(() => InstallAsync(gameRoot, proxyName, packagePath, token), token);
        return GetStatus();
    }

    public DlssUnlockedStatus StartUninstall(string installationId)
    {
        lock (_sync)
        {
            if (_busy)
            {
                return GetStatus();
            }
            var installation = ReadInstallations().FirstOrDefault(item =>
                item.Id.Equals(installationId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("找不到该 DLSS 安装记录。");
            _busy = true;
            _operation = "正在恢复游戏原始文件…";
            _error = null;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _ = Task.Run(() => UninstallAsync(installation, token), token);
        }
        return GetStatus();
    }

    public void OpenUpstream()
    {
        Process.Start(new ProcessStartInfo(UpstreamUrl) { UseShellExecute = true });
    }

    public void OpenInstallation(string installationId)
    {
        var installation = ReadInstallations().FirstOrDefault(item =>
            item.Id.Equals(installationId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("找不到该 DLSS 安装记录。");
        GpuUnlockService.OpenFolder(installation.GamePath);
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }

    private async Task DownloadPackageAsync(CancellationToken token)
    {
        var finalPath = Path.Combine(_packageDirectory, Path.GetFileName(DownloadUrl));
        var partPath = finalPath + ".part";
        try
        {
            using var client = CreateHttpClient();
            var existingLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl);
            if (existingLength > 0)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                    existingLength, null);
            }

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, token);
            if (existingLength > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                existingLength = 0;
            }
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentRange?.Length ?? PackageBytes;
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var destination = new FileStream(
                partPath,
                existingLength > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                true);
            var buffer = new byte[1024 * 1024];
            long received = existingLength;
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
                    _download = _download with
                    {
                        ReceivedBytes = received,
                        TotalBytes = total
                    };
                }
            }
            await destination.FlushAsync(token);
            destination.Close();
            lock (_sync)
            {
                _operation = "正在校验 SHA-256…";
            }
            var hash = await HashFileAsync(partPath, token);
            if (!hash.Equals(PackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partPath);
                throw new InvalidDataException($"安装包 SHA-256 不匹配：{hash}");
            }
            File.Move(partPath, finalPath, true);
            lock (_sync)
            {
                _verifiedPackagePath = finalPath;
                _download = _download with
                {
                    Running = false,
                    ReceivedBytes = PackageBytes,
                    TotalBytes = PackageBytes,
                    Error = null
                };
                _operation = "";
                _error = null;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                _download = _download with { Running = false, Error = "下载已取消" };
                _operation = "";
                _error = "下载已取消";
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _download = _download with { Running = false, Error = ex.Message };
                _operation = "";
                _error = ex.Message;
            }
            AppPaths.Log($"DLSS Unlocked download failed: {ex}");
        }
    }

    private async Task InstallAsync(
        string gameRoot,
        string proxyName,
        string packagePath,
        CancellationToken token)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"LocalDeck-dlss-{Guid.NewGuid():N}");
        string? backupRoot = null;
        List<DlssInstalledFile>? manifestFiles = null;
        var committed = false;
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(packagePath, staging), token);
            token.ThrowIfCancellationRequested();
            var packageRoot = FindPackageRoot(staging);
            var files = Directory.EnumerateFiles(
                packageRoot, "*", SearchOption.AllDirectories).ToList();
            if (!File.Exists(Path.Combine(packageRoot, "dxgi.dll")))
            {
                throw new InvalidDataException("安装包中没有 dxgi.dll。");
            }

            var id = Guid.NewGuid().ToString("N");
            var safeGameName = MakeSafeName(
                new DirectoryInfo(gameRoot).Name, 36);
            backupRoot = Path.Combine(
                _backupDirectory, $"{safeGameName}-{DateTime.Now:yyyyMMdd-HHmmss}-{id[..6]}");
            Directory.CreateDirectory(backupRoot);
            manifestFiles = [];
            var installed = 0;
            foreach (var source in files)
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(packageRoot, source);
                if (relative.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) &&
                    proxyName == "version")
                {
                    relative = "version.dll";
                }
                else if (relative.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) &&
                         proxyName == "dxgi")
                {
                    // Keep the normal proxy name.
                }

                var destination = SafeCombine(gameRoot, relative);
                var hadOriginal = File.Exists(destination);
                if (hadOriginal)
                {
                    var backup = SafeCombine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                manifestFiles.Add(new DlssInstalledFile(relative, hadOriginal));
                installed++;
                lock (_sync)
                {
                    _operation = $"正在部署 {installed}/{files.Count} 个文件…";
                }
            }

            var installation = new DlssInstallation(
                id,
                gameRoot,
                new DirectoryInfo(gameRoot).Name,
                DateTimeOffset.Now,
                Version,
                proxyName,
                backupRoot,
                manifestFiles);
            var current = ReadInstallations().ToList();
            current.Add(installation);
            SaveInstallations(current);
            committed = true;
            lock (_sync)
            {
                _busy = false;
                _operation = "";
                _error = null;
            }
        }
        catch (OperationCanceledException)
        {
            RollbackPartialInstall(gameRoot, backupRoot, manifestFiles, committed);
            lock (_sync)
            {
                _busy = false;
                _operation = "";
                _error = "安装已取消";
            }
        }
        catch (Exception ex)
        {
            RollbackPartialInstall(gameRoot, backupRoot, manifestFiles, committed);
            lock (_sync)
            {
                _busy = false;
                _operation = "";
                _error = ex.Message;
            }
            AppPaths.Log($"DLSS Unlocked install failed: {ex}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch
            {
                // Temporary extraction cleanup is best-effort.
            }
        }
    }

    private async Task UninstallAsync(
        DlssInstallation installation,
        CancellationToken token)
    {
        try
        {
            foreach (var file in installation.Files)
            {
                token.ThrowIfCancellationRequested();
                var destination = SafeCombine(installation.GamePath, file.RelativePath);
                if (file.HadOriginal)
                {
                    var backup = SafeCombine(installation.BackupPath, file.RelativePath);
                    if (File.Exists(backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(backup, destination, overwrite: true);
                    }
                }
                else if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }

            var remaining = ReadInstallations()
                .Where(item => !item.Id.Equals(installation.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SaveInstallations(remaining);
            try
            {
                if (Directory.Exists(installation.BackupPath))
                {
                    Directory.Delete(installation.BackupPath, recursive: true);
                }
            }
            catch
            {
                // A locked backup can be cleaned up later without breaking the install list.
            }
            lock (_sync)
            {
                _busy = false;
                _operation = "";
                _error = null;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                _busy = false;
                _operation = "";
                _error = "还原已取消";
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _busy = false;
                _operation = "";
                _error = ex.Message;
            }
            AppPaths.Log($"DLSS Unlocked uninstall failed: {ex}");
        }
        await Task.CompletedTask;
    }

    private HttpClient CreateHttpClient()
    {
        var port = _proxyPort();
        var handler = new HttpClientHandler();
        if (port is > 0 and <= 65535)
        {
            handler.Proxy = new WebProxy($"http://127.0.0.1:{port}");
            handler.UseProxy = true;
        }
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string FindPackageRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, "dxgi.dll")))
        {
            return staging;
        }
        return Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories)
                   .FirstOrDefault(directory =>
                       File.Exists(Path.Combine(directory, "dxgi.dll")))
               ?? throw new InvalidDataException("DLSS Unlocked 压缩包结构无效。");
    }

    private static string ValidateGameDirectory(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException("请选择游戏目录。");
        }
        var root = Path.GetFullPath(gameDirectory);
        if (!Directory.Exists(root) ||
            Path.GetPathRoot(root)?.TrimEnd('\\') == root.TrimEnd('\\'))
        {
            throw new ArgumentException("请选择具体的游戏可执行文件目录，而不是磁盘根目录。");
        }
        if (!Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly).Any() &&
            !Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
                .Take(20).Any())
        {
            throw new ArgumentException("所选目录中没有找到游戏 exe。");
        }
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!combined.StartsWith(
                fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"安装包路径越界：{relative}");
        }
        return combined;
    }

    private static void RollbackPartialInstall(
        string gameRoot,
        string? backupRoot,
        IReadOnlyList<DlssInstalledFile>? files,
        bool committed)
    {
        if (committed || files is null || backupRoot is null)
        {
            return;
        }

        foreach (var file in files.Reverse())
        {
            try
            {
                var destination = SafeCombine(gameRoot, file.RelativePath);
                if (file.HadOriginal)
                {
                    var backup = SafeCombine(backupRoot, file.RelativePath);
                    if (File.Exists(backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(backup, destination, overwrite: true);
                    }
                }
                else if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log(
                    $"DLSS partial rollback failed for {file.RelativePath}: {ex.Message}");
            }
        }

        try
        {
            if (Directory.Exists(backupRoot))
            {
                Directory.Delete(backupRoot, recursive: true);
            }
        }
        catch
        {
            // Keep an unused backup if a file is still locked.
        }
    }

    private List<DlssInstallation> ReadInstallations()
    {
        try
        {
            return File.Exists(_installationFile)
                ? JsonSerializer.Deserialize<List<DlssInstallation>>(
                    File.ReadAllText(_installationFile), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex)
        {
            AppPaths.Log($"DLSS install manifest read failed: {ex.Message}");
            return [];
        }
    }

    private void SaveInstallations(List<DlssInstallation> installations)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_installationFile)!);
        File.WriteAllText(
            _installationFile,
            JsonSerializer.Serialize(installations, JsonOptions));
    }

    private static bool IsGpuSupported()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            return searcher.Get().Cast<System.Management.ManagementObject>()
                .Any(item => item["Name"]?.ToString()?.Contains(
                    "NVIDIA GeForce RTX", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash);
    }

    private bool IsCachedPackageVerified(string path)
    {
        if (_verifiedPackagePath?.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }
        if (_invalidPackagePath?.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (hash.Equals(PackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                _verifiedPackagePath = path;
                _invalidPackagePath = null;
                return true;
            }
            _invalidPackagePath = path;
            _error = "缓存的 DLSS Unlocked 安装包 SHA-256 不匹配，请重新下载。";
        }
        catch (Exception ex)
        {
            _error = $"无法校验 DLSS Unlocked 缓存：{ex.Message}";
        }
        return false;
    }

    private static string MakeSafeName(string name, int maximum)
    {
        var result = new string(name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
        return result[..Math.Min(result.Length, maximum)];
    }
}

public sealed record DlssUnlockedStatus(
    string Version,
    string DownloadUrl,
    string PackageSha256,
    string PackagePath,
    bool PackageReady,
    DownloadStatus Download,
    bool Busy,
    string Operation,
    string? Error,
    bool GpuSupported,
    IReadOnlyList<DlssInstallation> Installations);

public sealed record DlssInstallation(
    string Id,
    string GamePath,
    string DisplayName,
    DateTimeOffset InstalledAt,
    string Version,
    string ProxyName,
    string BackupPath,
    IReadOnlyList<DlssInstalledFile> Files);

public sealed record DlssInstalledFile(string RelativePath, bool HadOriginal);
