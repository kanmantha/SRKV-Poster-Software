using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DailyPosterGenerator.Models;

namespace DailyPosterGenerator.Services;

public record GeneratedCopy(string Caption, string Hashtags, string Provider);

public interface ITextGenerationService
{
    Task<bool> IsConfiguredAsync();
    Task<GeneratedCopy> GenerateAsync(Poster poster, CancellationToken ct = default);
}

/// <summary>Builds captions and hashtags locally when no AI provider is configured.</summary>
public class TemplateTextGenerationService : ITextGenerationService
{
    public Task<bool> IsConfiguredAsync() => Task.FromResult(false);

    public Task<GeneratedCopy> GenerateAsync(Poster poster, CancellationToken ct = default)
    {
        var title = poster.EventTitle;
        var category = string.IsNullOrWhiteSpace(poster.Category) ? "onthisday" : poster.Category;
        var date = poster.EventDate.ToString("MMMM d");

        var caption = BuildCaption(title, poster.Description, category, date);
        var hashtags = BuildHashtags(title, category);

        return Task.FromResult(new GeneratedCopy(caption, hashtags, "built-in-template"));
    }

    private static string BuildCaption(string title, string description, string category, string date)
    {
        var sb = new StringBuilder();
        sb.Append($"On {date}, {title}. ");

        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append($"{description} ");
        }

        sb.Append(category switch
        {
            "births" => "A remarkable day that gave the world a legendary talent.",
            "holidays" => "A moment worth celebrating and sharing.",
            "history" or "events" => "History reminds us that every day writes a new chapter.",
            "deaths" => "A day to honor those whose legacy lives on.",
            _ => "A perfect reason to pause, reflect, and share something meaningful."
        });

        return sb.ToString();
    }

    private static string BuildHashtags(string title, string category)
    {
        var tags = new List<string> { "#OnThisDay", "#DailyPoster", $"#{ToTag(category)}" };

        var words = Regex.Split(title, @"[^A-Za-z0-9]+")
            .Where(w => w.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4);

        foreach (var word in words)
        {
            tags.Add("#" + ToTag(word));
        }

        return string.Join(" ", tags);
    }

    private static string ToTag(string value) => Regex.Replace(value, @"[^A-Za-z0-9]", "").Trim();
}

/// <summary>
/// Generates captions and hashtags through any OpenAI-compatible chat completions
/// endpoint (OpenAI, Azure OpenAI, Groq, Ollama, LM Studio, ...).
/// </summary>
public class OpenAiTextGenerationService : ITextGenerationService
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ILogger<OpenAiTextGenerationService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiTextGenerationService(HttpClient http, ISettingsService settings, ILogger<OpenAiTextGenerationService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> IsConfiguredAsync() =>
        !string.IsNullOrWhiteSpace(await _settings.GetAsync("ai.apiKey"));

    public async Task<GeneratedCopy> GenerateAsync(Poster poster, CancellationToken ct = default)
    {
        var endpoint = (await _settings.GetAsync("ai.endpoint", "https://api.openai.com/v1"))!.TrimEnd('/');
        var apiKey = await _settings.GetAsync("ai.apiKey");
        var model = await _settings.GetAsync("ai.chatModel", "gpt-4o-mini");
        var timeoutSeconds = int.Parse(await _settings.GetAsync("ai.timeoutSeconds", "90") ?? "90");

        var date = poster.EventDate.ToString("MMMM d, yyyy");
        var prompt = BuildPrompt(poster, date);

        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = "You are a social media copywriter and poster designer. Always answer with valid JSON only: {\"caption\": \"...\", \"hashtags\": [\"#a\", \"#b\"]}. Keep the caption under 220 characters, engaging and warm, and provide 5 to 8 relevant hashtags." },
                new { role = "user", content = prompt }
            },
            temperature = 0.9,
            max_tokens = 300,
            response_format = new { type = "json_object" }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)));
        using var response = await _http.SendAsync(request, timeoutCts.Token);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("AI text API returned {Status}: {Body}", response.StatusCode, Truncate(body, 300));
            throw new InvalidOperationException($"AI text service failed ({response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        var (caption, hashtags) = ParseGeneratedContent(content);
        return new GeneratedCopy(caption, hashtags, $"{model} (OpenAI-compatible)");
    }

    private static string BuildPrompt(Poster poster, string date)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Date: {date}");
        sb.AppendLine($"Main event: {poster.EventTitle}");
        if (!string.IsNullOrWhiteSpace(poster.Description))
        {
            sb.AppendLine($"Details: {poster.Description}");
        }
        sb.AppendLine($"Category: {poster.Category ?? "event"}");
        sb.AppendLine("Write one engaging social caption and hashtags for this poster.");
        return sb.ToString();
    }

    private (string Caption, string Hashtags) ParseGeneratedContent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("AI text service returned empty content.");
        }

        var jsonStart = raw.IndexOf('{');
        var jsonEnd = raw.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var slice = raw[jsonStart..(jsonEnd + 1)];
            try
            {
                using var doc = JsonDocument.Parse(slice);
                var root = doc.RootElement;
                var caption = root.TryGetProperty("caption", out var cap) ? cap.GetString() : string.Empty;
                var tags = new List<string>();
                if (root.TryGetProperty("hashtags", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in arr.EnumerateArray())
                    {
                        var tag = t.GetString();
                        if (!string.IsNullOrWhiteSpace(tag))
                        {
                            tags.Add(tag.StartsWith('#') ? tag : "#" + tag);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(caption))
                {
                    return (caption, string.Join(" ", tags));
                }
            }
            catch (JsonException)
            {
                // fall through to regex cleanup
            }
        }

        var cleaned = Regex.Replace(raw, @"[\r\n]+", " ");
        var hashMatches = Regex.Matches(cleaned, @"#\w+");
        var hashtags = string.Join(" ", hashMatches.Cast<Match>().Select(m => m.Value).Distinct());
        var fallbackCaption = Regex.Replace(cleaned, @"#\w+", "").Trim().TrimEnd(',').Trim();

        return (fallbackCaption, hashtags);
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max] + "...";
    }
}

/// <summary>Selects the AI provider when configured, otherwise falls back to the local template.</summary>
public class CompositeTextGenerationService : ITextGenerationService
{
    private readonly OpenAiTextGenerationService _ai;
    private readonly TemplateTextGenerationService _template;
    private readonly ISettingsService _settings;

    public CompositeTextGenerationService(
        OpenAiTextGenerationService ai,
        TemplateTextGenerationService template,
        ISettingsService settings)
    {
        _ai = ai;
        _template = template;
        _settings = settings;
    }

    public Task<bool> IsConfiguredAsync() => _ai.IsConfiguredAsync();

    public async Task<GeneratedCopy> GenerateAsync(Poster poster, CancellationToken ct = default)
    {
        var enabled = bool.Parse(await _settings.GetAsync("ai.enabled", "true") ?? "true");
        if (enabled && await _ai.IsConfiguredAsync())
        {
            return await _ai.GenerateAsync(poster, ct);
        }

        return await _template.GenerateAsync(poster, ct);
    }
}
