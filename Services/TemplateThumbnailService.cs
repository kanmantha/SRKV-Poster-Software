using DailyPosterGenerator.Models;

namespace DailyPosterGenerator.Services;

public interface ITemplateThumbnailService
{
    Task<string?> EnsureThumbnailAsync(PosterTemplate template, CancellationToken ct = default);
}

/// <summary>
/// Renders a small preview image for a poster template using the same SkiaSharp
/// pipeline, caching the result under wwwroot/templates/.
/// </summary>
public class TemplateThumbnailService : ITemplateThumbnailService
{
    private readonly IPosterImageService _image;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<TemplateThumbnailService> _logger;

    public TemplateThumbnailService(
        IPosterImageService image,
        IWebHostEnvironment env,
        ILogger<TemplateThumbnailService> logger)
    {
        _image = image;
        _env = env;
        _logger = logger;
    }

    public async Task<string?> EnsureThumbnailAsync(PosterTemplate template, CancellationToken ct = default)
    {
        var webRoot = _env.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return null;
        }

        var fileName = $"template_{template.Id}.png";
        var dir = Path.Combine(webRoot, "templates");
        Directory.CreateDirectory(dir);
        var finalPath = Path.Combine(dir, fileName);
        if (File.Exists(finalPath))
        {
            return $"/templates/{fileName}";
        }

        var sample = new Poster
        {
            Id = 0,
            TenantId = 0,
            Title = "On This Day",
            EventTitle = "A moment worth remembering",
            Category = "History",
            EventDate = DateTime.Today,
            Caption = "Discover the stories that shaped today.",
            Hashtags = "#OnThisDay #DailyPoster",
            Status = PosterStatus.Ready
        };

        try
        {
            var imagePath = await _image.GenerateAsync(sample, template.Theme, template.AccentColor, template, logosOnly: false, ct);
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            var full = Path.Combine(webRoot, imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                return null;
            }

            File.Copy(full, finalPath, overwrite: true);
            try
            {
                File.Delete(full);
            }
            catch
            {
                // The sample render can stay; it is harmless.
            }

            return $"/templates/{fileName}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail generation failed for template {TemplateId}", template.Id);
            return null;
        }
    }
}