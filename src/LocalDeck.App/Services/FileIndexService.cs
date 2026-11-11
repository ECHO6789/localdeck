using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace LocalDeck.App.Services;

public sealed class FileIndexService
{
    private readonly object _sync = new();
    private CancellationTokenSource? _indexCancellation;
    private bool _indexing;
    private string _indexError = string.Empty;
    private DateTimeOffset? _lastIndexed;

    public FileIndexService()
    {
        EnsureSchema();
    }

    public FileIndexStatus GetStatus()
    {
        lock (_sync)
        {
            var count = CountFiles();
            _lastIndexed ??= ReadMeta("index_completed");
            if (_lastIndexed is null && count > 0 && File.Exists(AppPaths.DatabasePath))
            {
                _lastIndexed = new DateTimeOffset(File.GetLastWriteTimeUtc(AppPaths.DatabasePath));
            }

            return new FileIndexStatus(
                _indexing,
                count,
                _lastIndexed,
                string.IsNullOrWhiteSpace(_indexError) ? null : _indexError);
        }
    }

    public void StartIndex(IReadOnlyList<string> roots)
    {
        if (_indexing)
        {
            return;
        }

        _indexCancellation = new CancellationTokenSource();
        _indexing = true;
        _indexError = string.Empty;
        var cancellationToken = _indexCancellation.Token;
        _ = Task.Run(() => IndexCore(roots, cancellationToken), cancellationToken);
    }

    public void StopIndex()
    {
        _indexCancellation?.Cancel();
    }

    public IReadOnlyList<FileSearchResult> Search(string query, int limit = 80)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(6)
            .Select(term => $"\"{term.Replace("\"", "\"\"")}\"*");
        var expression = string.Join(" AND ", terms);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.path, f.name, f.dir, f.size, f.modified
            FROM file_fts
            JOIN files f ON f.path = file_fts.path
            WHERE file_fts MATCH $query
            ORDER BY rank, f.modified DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", expression);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 300));
        using var reader = command.ExecuteReader();
        var result = new List<FileSearchResult>();
        while (reader.Read())
        {
            result.Add(new FileSearchResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4))));
        }

        return result;
    }

    public bool Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return true;
    }

    public bool Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true
        });
        return true;
    }

    private void IndexCore(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var completed = false;
        try
        {
            using var connection = OpenConnection();
            using (var clear = connection.CreateCommand())
            {
                clear.CommandText = "DELETE FROM file_fts; DELETE FROM files;";
                clear.ExecuteNonQuery();
            }

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false
            };

            var batch = new List<IndexedFile>(500);
            foreach (var root in roots.Where(Directory.Exists))
            {
                var files = Directory.EnumerateFiles(root, "*", options);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ShouldSkip(file))
                    {
                        continue;
                    }

                    try
                    {
                        var info = new FileInfo(file);
                        batch.Add(new IndexedFile(
                            info.FullName,
                            info.Name,
                            info.DirectoryName ?? string.Empty,
                            info.Length,
                            new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()));
                    }
                    catch
                    {
                        // Files can disappear while a drive is being indexed.
                    }

                    if (batch.Count >= 500)
                    {
                        Flush(connection, batch);
                        batch.Clear();
                    }
                }
            }

            Flush(connection, batch);
            _lastIndexed = DateTimeOffset.Now;
            WriteMeta(connection, "index_completed", _lastIndexed.Value.ToString("O"));
            completed = true;
        }
        catch (OperationCanceledException)
        {
            _indexError = "索引已停止";
        }
        catch (Exception ex)
        {
            _indexError = ex.Message;
            AppPaths.Log($"File index failed: {ex}");
        }
        finally
        {
            if (!completed)
            {
                _lastIndexed ??= ReadMeta("index_completed");
            }
            stopwatch.Stop();
            _indexing = false;
            AppPaths.Log($"File index finished in {stopwatch.Elapsed.TotalSeconds:F1}s.");
        }
    }

    private static void Flush(SqliteConnection connection, List<IndexedFile> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using var fileCommand = connection.CreateCommand();
        fileCommand.Transaction = transaction;
        fileCommand.CommandText = """
            INSERT OR REPLACE INTO files(path, name, dir, size, modified)
            VALUES($path, $name, $dir, $size, $modified);
            """;
        var path = fileCommand.Parameters.Add("$path", SqliteType.Text);
        var name = fileCommand.Parameters.Add("$name", SqliteType.Text);
        var dir = fileCommand.Parameters.Add("$dir", SqliteType.Text);
        var size = fileCommand.Parameters.Add("$size", SqliteType.Integer);
        var modified = fileCommand.Parameters.Add("$modified", SqliteType.Integer);

        using var searchCommand = connection.CreateCommand();
        searchCommand.Transaction = transaction;
        searchCommand.CommandText = """
            INSERT INTO file_fts(path, name, dir) VALUES($path, $name, $dir);
            """;
        var searchPath = searchCommand.Parameters.Add("$path", SqliteType.Text);
        var searchName = searchCommand.Parameters.Add("$name", SqliteType.Text);
        var searchDir = searchCommand.Parameters.Add("$dir", SqliteType.Text);

        foreach (var item in batch)
        {
            path.Value = item.Path;
            name.Value = item.Name;
            dir.Value = item.Directory;
            size.Value = item.Size;
            modified.Value = item.Modified;
            fileCommand.ExecuteNonQuery();

            searchPath.Value = item.Path;
            searchName.Value = item.Name;
            searchDir.Value = item.Directory;
            searchCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static bool ShouldSkip(string path)
    {
        return path.Contains(@"\node_modules\", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(@"\Windows\WinSxS\", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(@"\pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(@"\hiberfil.sys", StringComparison.OrdinalIgnoreCase);
    }

    private int CountFiles()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM files;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch
        {
            return 0;
        }
    }

    private static DateTimeOffset? ReadMeta(string key)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            var value = command.ExecuteScalar() as string;
            return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meta(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenConnection()
    {
        AppPaths.EnsureCreated();
        var connection = new SqliteConnection($"Data Source={AppPaths.DatabasePath};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureSchema()
    {
        AppPaths.EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS files(
                path TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                dir TEXT NOT NULL,
                size INTEGER NOT NULL,
                modified INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS meta(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS file_fts USING fts5(
                path UNINDEXED,
                name,
                dir,
                tokenize='unicode61'
            );
            """;
        command.ExecuteNonQuery();
    }

    private sealed record IndexedFile(
        string Path, string Name, string Directory, long Size, long Modified);
}

public sealed record FileIndexStatus(
    bool Indexing,
    int FileCount,
    DateTimeOffset? LastIndexed,
    string? Error);

public sealed record FileSearchResult(
    string Path,
    string Name,
    string Directory,
    long Size,
    DateTimeOffset Modified);
