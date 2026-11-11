namespace LocalDeck.App.Services;

public sealed class DiskAnalyzerService : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task? _scanTask;
    private DiskAnalysisStatus _status = new(false, null, 0, 0, 0, null, []);

    public DiskAnalysisStatus GetStatus()
    {
        lock (_sync)
        {
            return _status;
        }
    }

    public DiskAnalysisStatus Start(string root)
    {
        lock (_sync)
        {
            if (_status.Running)
            {
                return _status;
            }

            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException(root);
            }

            _cancellation = new CancellationTokenSource();
            _status = new DiskAnalysisStatus(true, root, 0, 0, 0, root, []);
            var token = _cancellation.Token;
            _scanTask = Task.Run(() => Scan(root, token), token);
            return _status;
        }
    }

    public void Cancel()
    {
        _cancellation?.Cancel();
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }

    private void Scan(string root, CancellationToken token)
    {
        var aggregate = new Dictionary<string, DiskUsageGroup>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        long fileCount = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };

        try
        {
            var directories = Directory.EnumerateDirectories(root, "*", options).ToList();
            directories.Insert(0, root);
            foreach (var directory in directories)
            {
                token.ThrowIfCancellationRequested();
                var label = directory.Equals(root, StringComparison.OrdinalIgnoreCase)
                    ? "(根目录文件)"
                    : Path.GetFileName(directory.TrimEnd('\\'));
                var recurse = !directory.Equals(root, StringComparison.OrdinalIgnoreCase);
                var (groupSize, groupFiles) = MeasureDirectory(directory, recurse, token);
                totalSize += groupSize;
                fileCount += groupFiles;

                aggregate[label] = new DiskUsageGroup(label, directory, groupSize, groupFiles);
                lock (_sync)
                {
                    _status = _status with
                    {
                        TotalBytes = totalSize,
                        FileCount = fileCount,
                        IndexedGroups = aggregate.Count,
                        CurrentPath = directory,
                        Items = aggregate.Values
                            .OrderByDescending(item => item.SizeBytes)
                            .Take(100)
                            .ToList()
                    };
                }
            }

            lock (_sync)
            {
                _status = _status with
                {
                    Running = false,
                    CurrentPath = null,
                    Items = aggregate.Values.OrderByDescending(item => item.SizeBytes).Take(100).ToList()
                };
            }
            AppPaths.Log($"Disk analysis {root} completed in {stopwatch.Elapsed.TotalSeconds:F1}s.");
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                _status = _status with { Running = false, CurrentPath = null };
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _status = _status with { Running = false, Error = ex.Message, CurrentPath = null };
            }
            AppPaths.Log($"Disk analysis failed: {ex}");
        }
    }

    private static (long Size, long Files) MeasureDirectory(
        string directory, bool recurse, CancellationToken token)
    {
        long size = 0;
        long files = 0;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            RecurseSubdirectories = recurse,
            ReturnSpecialDirectories = false
        };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                size += new FileInfo(file).Length;
                files++;
            }
            catch
            {
                // Files can disappear during a live scan.
            }
        }
        return (size, files);
    }
}

public sealed record DiskAnalysisStatus(
    bool Running,
    string? Root,
    long TotalBytes,
    long FileCount,
    int IndexedGroups,
    string? CurrentPath,
    IReadOnlyList<DiskUsageGroup> Items,
    string? Error = null);

public sealed record DiskUsageGroup(
    string Name,
    string Path,
    long SizeBytes,
    long FileCount);
