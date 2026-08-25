using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DailyPosterGenerator.Models;

namespace DailyPosterGenerator.Services;

public interface IEventService
{
    Task<List<EventItem>> GetTodaysEventsAsync(DateTime date, CancellationToken ct = default);
}

public class WikipediaEventService : IEventService
{
    private readonly HttpClient _http;
    private readonly ILogger<WikipediaEventService> _logger;
    private readonly int _maxEvents;

    public WikipediaEventService(HttpClient http, ILogger<WikipediaEventService> logger, IConfiguration config)
    {
        _http = http;
        _logger = logger;
        _maxEvents = config.GetValue("WikipediaEvents:MaxEvents", 8);
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("DailyPosterGenerator/1.0 (local demo; contact@example.com)");
        _http.Timeout = TimeSpan.FromSeconds(25);
    }

    public async Task<List<EventItem>> GetTodaysEventsAsync(DateTime date, CancellationToken ct = default)
    {
        var month = date.Month;
        var day = date.Day;

        // The "all" type is unreliable; query individual types in parallel instead.
        var types = new[] { "selected", "events", "births", "holidays" };
        var hosts = new[] { "en.wikipedia.org", "api.wikimedia.org" };

        var results = new List<EventItem>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        foreach (var host in hosts)
        {
            if (results.Count > 0)
            {
                break;
            }

            var tasks = types.Select(type => FetchTypeAsync(host, type, month, day, timeoutCts.Token));
            var batches = await Task.WhenAll(tasks);
            results = Merge(batches);
        }

        return results.Count > 0 ? results : OfflineEventCalendar.GetEvents(date, _maxEvents);
    }

    private async Task<List<EventItem>> FetchTypeAsync(string host, string type, int month, int day, CancellationToken ct)
    {
        var endpoint = $"https://{host}/api/rest_v1/feed/onthisday/{type}/{month}/{day}";
        try
        {
            using var response = await _http.GetAsync(endpoint, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Event API {Endpoint} returned {Status}", endpoint, response.StatusCode);
                return new List<EventItem>();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return Parse(json, type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Event API {Endpoint} failed", endpoint);
            return new List<EventItem>();
        }
    }

    private List<EventItem> Merge(IReadOnlyList<List<EventItem>> batches)
    {
        var result = new List<EventItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in batches)
        {
            foreach (var item in batch)
            {
                if (seen.Add(item.Text))
                {
                    result.Add(item);
                }

                if (result.Count >= _maxEvents)
                {
                    return result;
                }
            }
        }

        return result;
    }

    private List<EventItem> Parse(string json, string category)
    {
        var result = new List<EventItem>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var selectedTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty(category, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("text", out var textEl) || string.IsNullOrWhiteSpace(textEl.GetString()))
                {
                    continue;
                }

                var text = CleanText(textEl.GetString()!);
                if (string.IsNullOrWhiteSpace(text) || text.Length < 12 || !selectedTexts.Add(text))
                {
                    continue;
                }

                int? year = item.TryGetProperty("year", out var yearEl) && yearEl.ValueKind == JsonValueKind.Number
                    ? yearEl.GetInt32()
                    : null;

                string? url = null;
                if (item.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var page in pages.EnumerateArray())
                    {
                        if (page.TryGetProperty("content_urls", out var cu) &&
                            cu.TryGetProperty("desktop", out var d) &&
                            d.TryGetProperty("page", out var p))
                        {
                            url = p.GetString();
                            break;
                        }
                    }
                }

                result.Add(new EventItem
                {
                    Text = text,
                    Year = year,
                    Kind = category,
                    Url = url
                });

                if (result.Count >= _maxEvents)
                {
                    return result;
                }
            }

        return result;
    }

    private static string CleanText(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"[.!?]\s*$", "").TrimEnd();
        return text;
    }
}
