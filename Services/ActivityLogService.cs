using System.Collections.Concurrent;

namespace DailyPosterGenerator.Services;

/// <summary>Single log entry shown on the Logs page.</summary>
public record LogEntry(DateTime Timestamp, string Level, string Category, string Message);

public interface IActivityLog
{
    void Add(string level, string message);

    IReadOnlyList<LogEntry> GetRecent(int count = 200);

    void Clear();
}

/// <summary>
/// In-memory, thread-safe ring buffer of application activity (scheduler runs,
/// generation results, publish operations) surfaced in the web UI Logs page.
/// </summary>
public class ActivityLogService : IActivityLog
{
    private const int Capacity = 500;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public void Add(string level, string message)
    {
        var entry = new LogEntry(DateTime.Now, Normalize(level), ResolveCategory(level), message);
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<LogEntry> GetRecent(int count = 200)
    {
        return _entries.OrderByDescending(e => e.Timestamp).Take(count).ToList();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
        }
    }

    private static string Normalize(string level) => level.ToUpperInvariant() switch
    {
        "ERROR" => "error",
        "WARN" or "WARNING" => "warning",
        "INFO" or "INFORMATION" => "info",
        _ => "info"
    };

    private static string ResolveCategory(string level) => level.ToLowerInvariant() switch
    {
        "scheduler" => "Scheduler",
        "generate" => "Generation",
        "error" => "Error",
        "publish" => "Publish",
        "settings" => "Settings",
        _ => "System"
    };
}
