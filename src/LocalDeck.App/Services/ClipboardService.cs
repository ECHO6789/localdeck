using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace LocalDeck.App.Services;

public sealed class ClipboardService
{
    private readonly object _sync = new();
    private bool _suppressNext;
    public bool CaptureImages { get; set; } = true;

    public ClipboardService()
    {
        EnsureSchema();
    }

    public void CaptureCurrentClipboard()
    {
        if (_suppressNext)
        {
            _suppressNext = false;
            return;
        }

        lock (_sync)
        {
            try
            {
                if (Clipboard.ContainsImage())
                {
                    if (!CaptureImages)
                    {
                        return;
                    }

                    var image = Clipboard.GetImage();
                    if (image is not null)
                    {
                        SaveImage(image);
                    }

                    return;
                }

                var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                var html = Clipboard.ContainsData(DataFormats.Html)
                    ? Clipboard.GetData(DataFormats.Html) as string
                    : null;
                var rtf = Clipboard.ContainsData(DataFormats.Rtf)
                    ? Clipboard.GetData(DataFormats.Rtf) as string
                    : null;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    SaveText("text", text, text, html, rtf);
                }
                else if (!string.IsNullOrWhiteSpace(html))
                {
                    SaveText("html", html, StripHtml(html), html, rtf);
                }
                else if (!string.IsNullOrWhiteSpace(rtf))
                {
                    SaveText("rtf", rtf, StripRtf(rtf), html, rtf);
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Clipboard capture failed: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<ClipboardEntry> GetHistory(string? filter = null, int limit = 200)
    {
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, kind, payload, created_at, pinned
                FROM clipboard_entries
                ORDER BY pinned DESC, created_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            using var reader = command.ExecuteReader();
            var result = new List<ClipboardEntry>();
            while (reader.Read())
            {
                try
                {
                    var payload = Dpapi.Unprotect((byte[])reader["payload"]);
                    var kind = reader.GetString(1);
                    var text = kind == "image"
                        ? $"图片 {GetPngDimensions(payload)}"
                        : Encoding.UTF8.GetString(payload);
                    var preview = text.ReplaceLineEndings(" ").Trim();
                    if (preview.Length > 180)
                    {
                        preview = preview[..180] + "…";
                    }

                    if (!string.IsNullOrWhiteSpace(filter) &&
                        !text.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(new ClipboardEntry(
                        reader.GetInt64(0),
                        kind,
                        preview,
                        DateTimeOffset.Parse(reader.GetString(3)),
                        reader.GetInt64(4) != 0,
                        text.Length));
                }
                catch (Exception ex)
                {
                    AppPaths.Log($"Clipboard row decode failed: {ex.Message}");
                }
            }

            return result;
        }
    }

    public bool Copy(long id)
    {
        lock (_sync)
        {
            try
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT kind, payload FROM clipboard_entries WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return false;
                }

                var kind = reader.GetString(0);
                var payload = Dpapi.Unprotect((byte[])reader.GetValue(1));
                _suppressNext = true;

                if (kind == "image")
                {
                    using var memory = new MemoryStream(payload);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = memory;
                    image.EndInit();
                    image.Freeze();
                    Clipboard.SetImage(image);
                }
                else
                {
                    Clipboard.SetText(Encoding.UTF8.GetString(payload));
                }

                return true;
            }
            catch (Exception ex)
            {
                _suppressNext = false;
                AppPaths.Log($"Clipboard paste failed: {ex.Message}");
                return false;
            }
        }
    }

    public IReadOnlyList<ClipboardEntry> AddSnippet(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return GetHistory();
        }

        SaveText("snippet", text, text, null, null, pinned: true);
        return GetHistory();
    }

    public IReadOnlyList<ClipboardEntry> TogglePinned(long id)
    {
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE clipboard_entries
                SET pinned = CASE WHEN pinned = 0 THEN 1 ELSE 0 END
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        return GetHistory();
    }

    public IReadOnlyList<ClipboardEntry> Delete(long id)
    {
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM clipboard_entries WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        return GetHistory();
    }

    public IReadOnlyList<ClipboardEntry> Clear(bool includePinned = false)
    {
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = includePinned
                ? "DELETE FROM clipboard_entries;"
                : "DELETE FROM clipboard_entries WHERE pinned = 0;";
            command.ExecuteNonQuery();
        }

        return GetHistory();
    }

    private void SaveText(
        string kind, string payload, string preview, string? html, string? rtf, bool pinned = false)
    {
        var combined = kind switch
        {
            "rtf" when !string.IsNullOrWhiteSpace(html) => html,
            _ => payload
        };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combined)));
        InsertEntry(kind, hash, Encoding.UTF8.GetBytes(payload), pinned);
    }

    private void SaveImage(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        InsertEntry("image", hash, bytes, pinned: false);
    }

    private void InsertEntry(string kind, string hash, byte[] payload, bool pinned)
    {
        if (payload.Length > 12 * 1024 * 1024)
        {
            return;
        }

        var encrypted = Dpapi.Protect(payload);
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clipboard_entries(kind, hash, payload, created_at, pinned)
                VALUES($kind, $hash, $payload, $created, $pinned)
                ON CONFLICT(hash) DO UPDATE SET created_at = excluded.created_at;
                """;
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$payload", encrypted);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            command.ExecuteNonQuery();

            using var trim = connection.CreateCommand();
            trim.CommandText = """
                DELETE FROM clipboard_entries
                WHERE pinned = 0 AND id NOT IN (
                    SELECT id FROM clipboard_entries
                    WHERE pinned = 0
                    ORDER BY created_at DESC
                    LIMIT 500
                );
                """;
            trim.ExecuteNonQuery();
        }
    }

    private static SqliteConnection OpenConnection()
    {
        AppPaths.EnsureCreated();
        var connection = new SqliteConnection($"Data Source={AppPaths.DatabasePath};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureSchema()
    {
        AppPaths.EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS clipboard_entries(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
                hash TEXT NOT NULL UNIQUE,
                payload BLOB NOT NULL,
                created_at TEXT NOT NULL,
                pinned INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_clipboard_created
                ON clipboard_entries(created_at DESC);
            """;
        command.ExecuteNonQuery();
    }

    private static string StripHtml(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    private static string StripRtf(string rtf)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(rtf, @"\\[a-zA-Z]+-?\d* ?", " ");
        text = text.Replace("{", string.Empty).Replace("}", string.Empty).Replace("\\", string.Empty);
        return text.Trim();
    }

    private static string GetPngDimensions(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != 0x50)
        {
            return "PNG";
        }

        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        return $"{width}×{height}";
    }
}

public sealed record ClipboardEntry(
    long Id,
    string Kind,
    string Preview,
    DateTimeOffset CreatedAt,
    bool Pinned,
    int CharacterCount);
