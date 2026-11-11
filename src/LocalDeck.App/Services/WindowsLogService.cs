using System.Diagnostics.Eventing.Reader;

namespace LocalDeck.App.Services;

public sealed class WindowsLogService
{
    public Task<WindowsLogReport> AnalyzeAsync(
        TimeSpan window, int limit = 400, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Analyze(window, limit, cancellationToken), cancellationToken);
    }

    private static WindowsLogReport Analyze(
        TimeSpan window, int limit, CancellationToken cancellationToken)
    {
        var since = DateTime.Now - window;
        var entries = new List<WindowsEventItem>();
        foreach (var logName in new[] { "System", "Application" })
        {
            try
            {
                var query = new EventLogQuery(
                    logName,
                    PathType.LogName,
                    "*[System[(Level=1 or Level=2 or Level=3)]]")
                {
                    ReverseDirection = true
                };
                using var reader = new EventLogReader(query);
                EventRecord? record;
                var perLog = 0;
                while ((record = reader.ReadEvent()) is not null &&
                       entries.Count < limit &&
                       perLog < limit)
                {
                    using (record)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var created = record.TimeCreated ?? DateTime.MinValue;
                        if (created < since)
                        {
                            continue;
                        }

                        entries.Add(new WindowsEventItem(
                            created,
                            logName,
                            record.LevelDisplayName ?? record.Level?.ToString() ?? "未知",
                            record.ProviderName ?? "",
                            record.Id,
                            TrimMessage(record.FormatDescription())));
                        perLog++;
                    }
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Windows log {logName} read failed: {ex.Message}");
            }
        }

        var groups = entries
            .GroupBy(item => new { item.LogName, item.Provider })
            .Select(group => new LogProviderGroup(
                group.Key.LogName,
                group.Key.Provider,
                group.Count(),
                group.Count(item => item.Level.Contains("错误", StringComparison.OrdinalIgnoreCase)),
                group.Max(item => item.Time)))
            .OrderByDescending(item => item.Count)
            .Take(30)
            .ToList();

        var dumps = new List<string>();
        var dumpDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
        if (Directory.Exists(dumpDirectory))
        {
            try
            {
                dumps.AddRange(Directory.EnumerateFiles(dumpDirectory, "*.dmp")
                    .OrderByDescending(File.GetLastWriteTime)
                    .Take(20));
            }
            catch
            {
                // Protected dump directories may be unreadable.
            }
        }

        return new WindowsLogReport(
            since,
            entries.OrderByDescending(item => item.Time).ToList(),
            groups,
            dumps);
    }

    private static string TrimMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "";
        }

        var normalized = message.ReplaceLineEndings(" ").Trim();
        return normalized.Length > 600 ? normalized[..600] + "…" : normalized;
    }
}

public sealed record WindowsLogReport(
    DateTime Since,
    IReadOnlyList<WindowsEventItem> Entries,
    IReadOnlyList<LogProviderGroup> Groups,
    IReadOnlyList<string> Minidumps);

public sealed record WindowsEventItem(
    DateTime Time,
    string LogName,
    string Level,
    string Provider,
    int EventId,
    string Message);

public sealed record LogProviderGroup(
    string LogName,
    string Provider,
    int Count,
    int ErrorCount,
    DateTime Latest);
