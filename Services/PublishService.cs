using System.Text;
using System.Text.Json;
using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Services;

public record PublishResult(bool Success, List<string> Platforms, string? Error);

public interface IPublishService
{
    Task<PublishResult> PublishAsync(Poster poster, CancellationToken ct = default);
}

public class PublishService : IPublishService
{
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PublishService> _logger;
    private readonly IActivityLog _log;

    public PublishService(
        IDbContextFactory<DailyPosterDbContext> dbFactory,
        IHttpClientFactory httpFactory,
        ILogger<PublishService> logger,
        IActivityLog log)
    {
        _dbFactory = dbFactory;
        _httpFactory = httpFactory;
        _logger = logger;
        _log = log;
    }

    public async Task<PublishResult> PublishAsync(Poster poster, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var platforms = await db.Platforms.AsNoTracking()
            .Where(p => p.Enabled)
            .ToListAsync(ct);

        var published = new List<string>();
        var errors = new List<string>();

        foreach (var platform in platforms)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(platform.WebhookUrl))
                {
                    await PostToWebhookAsync(platform, poster, ct);
                }
                else
                {
                    await Task.Delay(50, ct);
                }

                published.Add(platform.Name);
                _logger.LogInformation("Published poster #{Id} to {Platform}", poster.Id, platform.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Publish to {Platform} failed", platform.Name);
                errors.Add($"{platform.Name}: {ex.Message}");
            }
        }

        if (published.Count > 0 || errors.Count == 0)
        {
            poster.Status = PosterStatus.Published;
            poster.PublishedAt = DateTime.UtcNow;
            poster.PublishedPlatforms = string.Join(",", published);

            db.Posters.Update(poster);
            await db.SaveChangesAsync(ct);

            _log.Add("publish", $"Published poster #{poster.Id} ({poster.EventTitle}) to {string.Join(", ", published.Count > 0 ? published : new[] { "no platforms (marked)" })}.");

            return new PublishResult(true, published, errors.Count > 0 ? string.Join(" | ", errors) : null);
        }

        _log.Add("error", $"Publish failed for poster #{poster.Id} ({poster.EventTitle}): {string.Join(" | ", errors)}");
        return new PublishResult(false, published, errors.Count > 0 ? string.Join(" | ", errors) : "No enabled platforms.");
    }

    private async Task PostToWebhookAsync(Platform platform, Poster poster, CancellationToken ct)
    {
        var baseUrl = platform.WebhookUrl!;
        var http = _httpFactory.CreateClient("publish");
        http.Timeout = TimeSpan.FromSeconds(20);

        var absoluteImageUrl = ResolveAbsoluteUrl(baseUrl, poster.ImagePath);

        var payload = new Dictionary<string, object?>
        {
            ["posterId"] = poster.Id,
            ["title"] = poster.Title,
            ["event"] = poster.EventTitle,
            ["category"] = poster.Category,
            ["caption"] = poster.Caption,
            ["hashtags"] = poster.Hashtags,
            ["imageUrl"] = absoluteImageUrl,
            ["publishedAt"] = DateTime.UtcNow.ToString("O")
        };

        var response = await http.PostAsJsonAsync(baseUrl, payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Webhook returned {(int)response.StatusCode}");
        }
    }

    private string? ResolveAbsoluteUrl(string baseUrl, string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return imagePath;
        }

        return new Uri(baseUri, imagePath.TrimStart('/')).ToString();
    }
}
