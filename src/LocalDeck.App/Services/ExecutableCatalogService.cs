using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace LocalDeck.App.Services;

public sealed class ExecutableCatalogService
{
    private readonly object _scanSync = new();
    private bool _knownLocationsScanned;

    public ExecutableCatalogService()
    {
        EnsureSchema();
    }

    public IReadOnlyList<ExecutableItem> GetExecutables(string? query, int limit = 1200)
    {
        EnsureKnownLocationsIndexed();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH candidates AS (
                SELECT path, name, size, modified FROM files
                UNION
                SELECT path, name, size, modified FROM exe_catalog
            )
            SELECT c.path, c.name, c.size, c.modified,
                   COALESCE(u.usage_count, 0), COALESCE(u.pinned, 0)
            FROM candidates c
            LEFT JOIN exe_usage u ON u.path = c.path
            WHERE lower(c.name) LIKE '%.exe'
              AND lower(c.name) NOT LIKE '%unins%'
              AND lower(c.name) NOT LIKE '%uninstall%'
              AND lower(c.name) NOT LIKE '%crash%'
              AND lower(c.name) NOT LIKE '%easyanticheat%'
              AND lower(c.name) NOT LIKE '%battleye%'
              AND lower(c.name) NOT LIKE '%eosoverlay%'
              AND lower(c.name) NOT LIKE '%epiconlineservices%'
              AND lower(c.name) NOT LIKE '%epicgamesupdate%'
              AND lower(c.name) NOT LIKE '%eosbootstrapper%'
              AND lower(c.name) NOT LIKE '%epicwebhelper%'
              AND (lower(c.path) NOT LIKE '%\epic games\launcher\%'
                   OR lower(c.name) = 'epicgameslauncher.exe')
              AND ($query = '' OR lower(c.name) LIKE $query OR lower(c.path) LIKE $query)
            ORDER BY COALESCE(u.pinned, 0) DESC,
                     COALESCE(u.usage_count, 0) DESC,
                     c.name COLLATE NOCASE
            LIMIT $sqlLimit;
            """;
        var normalized = query?.Trim().ToLowerInvariant() ?? "";
        command.Parameters.AddWithValue("$query", normalized.Length == 0 ? "" : $"%{normalized}%");
        limit = Math.Clamp(limit, 50, 5000);
        command.Parameters.AddWithValue("$sqlLimit", Math.Clamp(limit * 5, 500, 5000));
        using var reader = command.ExecuteReader();
        var result = new List<ExecutableItem>();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            if (!File.Exists(path))
            {
                continue;
            }
            var name = reader.GetString(1);
            result.Add(new ExecutableItem(
                path,
                Path.GetFileNameWithoutExtension(name),
                Classify(path, name),
                reader.GetInt64(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                reader.GetInt32(4),
                reader.GetInt64(5) != 0));
        }

        return result
            .OrderBy(item => item.Pinned ? 0 : 1)
            .ThenBy(item => item.Kind switch
            {
                "game" => 0,
                "application" => 1,
                _ => 2
            })
            .ThenByDescending(item => item.UsageCount)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToList();
    }

    public bool Launch(ExecutableItem item, string arguments = "")
    {
        if (!File.Exists(item.Path))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.Path, arguments) { UseShellExecute = true });
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO exe_usage(path, usage_count, last_launched, pinned)
                VALUES($path, 1, $now, 0)
                ON CONFLICT(path) DO UPDATE SET
                    usage_count = usage_count + 1,
                    last_launched = excluded.last_launched;
                """;
            command.Parameters.AddWithValue("$path", item.Path);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Executable launch failed: {ex.Message}");
            return false;
        }
    }

    public bool TogglePin(ExecutableItem item)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO exe_usage(path, usage_count, last_launched, pinned)
            VALUES($path, 0, $now, 1)
            ON CONFLICT(path) DO UPDATE SET pinned = CASE pinned WHEN 0 THEN 1 ELSE 0 END;
            """;
        command.Parameters.AddWithValue("$path", item.Path);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    private void EnsureKnownLocationsIndexed()
    {
        lock (_scanSync)
        {
            if (_knownLocationsScanned)
            {
                return;
            }
            _knownLocationsScanned = true;
        }

        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in KnownExecutableRoots().Where(Directory.Exists))
            {
                foreach (var path in EnumerateExecutables(root, maxDepth: 6))
                {
                    paths.Add(path);
                    if (paths.Count >= 25_000)
                    {
                        break;
                    }
                }
                if (paths.Count >= 25_000)
                {
                    break;
                }
            }
            StoreCatalogEntries(paths);
            AppPaths.Log($"Executable catalog refreshed: {paths.Count} candidates.");
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Executable catalog refresh failed: {ex.Message}");
        }
    }

    private static IEnumerable<string> KnownExecutableRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps"),
            @"C:\Program Files (x86)\WeGameApps",
            @"C:\Program Files\WeGameApps",
            @"C:\Riot Games",
            @"C:\Program Files (x86)\Riot Games",
            @"C:\Program Files (x86)\Battle.net",
            @"C:\Games"
        };

        AddSteamRoots(roots);
        AddEpicRoots(roots);
        return roots.Where(path => !string.IsNullOrWhiteSpace(path));
    }

    private static void AddSteamRoots(ISet<string> roots)
    {
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in new[]
                 {
                     (RegistryHive.CurrentUser, @"Software\Valve\Steam"),
                     (RegistryHive.LocalMachine, @"Software\Valve\Steam"),
                     (RegistryHive.LocalMachine, @"Software\WOW6432Node\Valve\Steam")
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(path);
                var value = (key?.GetValue("SteamPath") ?? key?.GetValue("InstallPath")) as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    steamRoots.Add(value.Replace('/', '\\'));
                }
            }
            catch
            {
                // Steam is optional and registry access can be denied.
            }
        }

        foreach (var steamRoot in steamRoots)
        {
            var common = Path.Combine(steamRoot, "steamapps", "common");
            if (Directory.Exists(common))
            {
                roots.Add(common);
            }

            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
            {
                continue;
            }

            try
            {
                foreach (Match match in Regex.Matches(
                             File.ReadAllText(libraryFile), "\"path\"\\s+\"([^\"]+)\""))
                {
                    var library = match.Groups[1].Value
                        .Replace(@"\\", @"\")
                        .Replace('/', '\\');
                    var libraryCommon = Path.Combine(library, "steamapps", "common");
                    if (Directory.Exists(libraryCommon))
                    {
                        roots.Add(libraryCommon);
                    }
                }
            }
            catch
            {
                // A malformed or concurrently updated VDF should not block the launcher.
            }
        }
    }

    private static void AddEpicRoots(ISet<string> roots)
    {
        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
        {
            return;
        }

        try
        {
            foreach (var manifest in Directory.EnumerateFiles(manifests, "*.item"))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                    if (document.RootElement.TryGetProperty("InstallLocation", out var location) &&
                        location.GetString() is { } path &&
                        Directory.Exists(path))
                    {
                        roots.Add(path);
                    }
                }
                catch
                {
                    // Ignore stale or partially written manifests.
                }
            }
        }
        catch
        {
            // ProgramData access is best effort.
        }
    }

    private static IEnumerable<string> EnumerateExecutables(string root, int maxDepth)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (IsPotentialExecutable(file))
                {
                    yield return file;
                }
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                if (ShouldSkipDirectory(child))
                {
                    continue;
                }
                pending.Push((child, depth + 1));
            }
        }
    }

    private static bool ShouldSkipDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }
        catch
        {
            return true;
        }

        var lower = path.ToLowerInvariant();
        return lower.Contains(@"\node_modules") ||
               lower.Contains(@"\.git\") ||
               lower.Contains(@"\installer\cache") ||
               lower.Contains(@"\package cache") ||
               lower.Contains(@"\windows\winsxs") ||
               lower.Contains(@"\temp\") ||
               lower.Contains(@"\crashpad\");
    }

    private static bool IsPotentialExecutable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var lowerPath = path.ToLowerInvariant();
        if (lowerPath.Contains(@"\epic games\launcher\") && name != "epicgameslauncher")
        {
            return false;
        }
        return !name.Contains("unins") &&
               !name.Contains("uninstall") &&
               !name.Contains("crash") &&
               !name.Contains("easyanticheat") &&
               !name.Contains("battleye") &&
               !name.Contains("eosoverlay") &&
               !name.Contains("epiconlineservices") &&
               !name.Contains("epicgamesupdate") &&
               !name.Contains("eosbootstrapper") &&
               !name.Contains("epicwebhelper") &&
               !name.StartsWith("setup") &&
               !name.StartsWith("installer") &&
               !name.StartsWith("update") &&
               !name.StartsWith("vcredist") &&
               !name.StartsWith("dxsetup") &&
               name is not "msedgewebview2";
    }

    private static void StoreCatalogEntries(IEnumerable<string> paths)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var insertCatalog = connection.CreateCommand();
        insertCatalog.Transaction = transaction;
        insertCatalog.CommandText = """
            INSERT INTO exe_catalog(path, name, size, modified)
            VALUES($path, $name, $size, $modified)
            ON CONFLICT(path) DO UPDATE SET
                name = excluded.name,
                size = excluded.size,
                modified = excluded.modified;
            """;
        var pathParameter = insertCatalog.Parameters.Add("$path", SqliteType.Text);
        var nameParameter = insertCatalog.Parameters.Add("$name", SqliteType.Text);
        var sizeParameter = insertCatalog.Parameters.Add("$size", SqliteType.Integer);
        var modifiedParameter = insertCatalog.Parameters.Add("$modified", SqliteType.Integer);

        using var insertFile = connection.CreateCommand();
        insertFile.Transaction = transaction;
        insertFile.CommandText = """
            INSERT OR IGNORE INTO files(path, name, dir, size, modified)
            VALUES($path, $name, $dir, $size, $modified);
            """;
        var filePath = insertFile.Parameters.Add("$path", SqliteType.Text);
        var fileName = insertFile.Parameters.Add("$name", SqliteType.Text);
        var fileDirectory = insertFile.Parameters.Add("$dir", SqliteType.Text);
        var fileSize = insertFile.Parameters.Add("$size", SqliteType.Integer);
        var fileModified = insertFile.Parameters.Add("$modified", SqliteType.Integer);

        using var insertSearch = connection.CreateCommand();
        insertSearch.Transaction = transaction;
        insertSearch.CommandText = """
            INSERT INTO file_fts(path, name, dir) VALUES($path, $name, $dir);
            """;
        var searchPath = insertSearch.Parameters.Add("$path", SqliteType.Text);
        var searchName = insertSearch.Parameters.Add("$name", SqliteType.Text);
        var searchDirectory = insertSearch.Parameters.Add("$dir", SqliteType.Text);

        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                var modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
                var directory = info.DirectoryName ?? string.Empty;

                pathParameter.Value = info.FullName;
                nameParameter.Value = info.Name;
                sizeParameter.Value = info.Length;
                modifiedParameter.Value = modified;
                insertCatalog.ExecuteNonQuery();

                filePath.Value = info.FullName;
                fileName.Value = info.Name;
                fileDirectory.Value = directory;
                fileSize.Value = info.Length;
                fileModified.Value = modified;
                if (insertFile.ExecuteNonQuery() > 0)
                {
                    searchPath.Value = info.FullName;
                    searchName.Value = info.Name;
                    searchDirectory.Value = directory;
                    insertSearch.ExecuteNonQuery();
                }
            }
            catch
            {
                // Installed programs can be updated while the catalog is scanning.
            }
        }

        transaction.Commit();
    }

    private static string Classify(string path, string name)
    {
        var lower = path.ToLowerInvariant();
        if (lower.Contains(@"\epic games\launcher\"))
        {
            return "application";
        }
        if (lower.Contains(@"\steamapps\common\") ||
            lower.Contains(@"\epic games\") ||
            lower.Contains(@"\wegameapps\") ||
            lower.Contains(@"\riot games\") ||
            lower.Contains(@"\battle.net\") ||
            lower.Contains(@"\games\") ||
            name.Contains("launcher", StringComparison.OrdinalIgnoreCase) &&
            (lower.Contains("game") || lower.Contains("we game")))
        {
            return "game";
        }

        if (lower.Contains(@"\program files\") ||
            lower.Contains(@"\program files (x86)\") ||
            lower.Contains(@"\appdata\local\programs\") ||
            lower.Contains(@"\windowsapps\"))
        {
            return "application";
        }

        return "utility";
    }

    private static SqliteConnection OpenConnection()
    {
        AppPaths.EnsureCreated();
        var connection = new SqliteConnection($"Data Source={AppPaths.DatabasePath};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=4000; PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS exe_usage(
                path TEXT PRIMARY KEY,
                usage_count INTEGER NOT NULL DEFAULT 0,
                last_launched TEXT,
                pinned INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS exe_catalog(
                path TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                size INTEGER NOT NULL,
                modified INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS files(
                path TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                dir TEXT NOT NULL,
                size INTEGER NOT NULL,
                modified INTEGER NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS file_fts USING fts5(
                path UNINDEXED,
                name,
                dir,
                tokenize='unicode61'
            );
            CREATE INDEX IF NOT EXISTS idx_files_exe_name
                ON files(name);
            """;
        command.ExecuteNonQuery();
    }
}

public sealed record ExecutableItem(
    string Path,
    string Name,
    string Kind,
    long Size,
    DateTimeOffset Modified,
    int UsageCount,
    bool Pinned);
