using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Services;

public record GenerationResult(bool Success, Poster? Poster, string? Error);

/// <summary>Outcome of generating a poster on every available template at once.</summary>
public record BulkGenerationResult(int Total, int Created, int Skipped, IReadOnlyList<string> Errors);

public sealed class GenerateOptions
{
    public bool Persist { get; set; } = true;

    public int TenantId { get; set; } = 1;

    public int? TemplateId { get; set; }

    /// <summary>Admins may render posters on templates owned by any workspace.</summary>
    public bool CrossTenant { get; set; }

    /// <summary>
    /// When true the poster is re-rendered with the same background and logos
    /// but all text layers (headline, caption, hashtags, footer) are stripped,
    /// producing a clean visual with only the design and branding elements.
    /// </summary>
    public bool LogosOnly { get; set; }
}

public interface IPosterGenerationService
{
    /// <summary>Runs the full pipeline: events -> copy -> image -> database.</summary>
    Task<GenerationResult> GenerateAsync(
        DateTime date,
        IReadOnlyList<EventItem> events,
        GenerateOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Runs the full pipeline for a single event, producing one dedicated poster.</summary>
    Task<GenerationResult> GenerateEventAsync(
        DateTime date,
        EventItem item,
        GenerateOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Generates one poster per active template visible to the tenant for the given
    /// date, skipping templates that already have a poster that day.
    /// </summary>
    Task<BulkGenerationResult> GenerateAllTemplatesAsync(
        DateTime date,
        IReadOnlyList<EventItem> events,
        int tenantId,
        bool crossTenant = false,
        CancellationToken ct = default);

    Task<bool> HasPosterForAsync(DateTime date, int tenantId = 1);

    /// <summary>True when a poster already exists for the given day and event title.</summary>
    Task<bool> HasPosterForEventAsync(DateTime date, string eventTitle, int tenantId = 1);

    /// <summary>
    /// Renders a non-persisted preview of a poster for the given date and events on the
    /// selected template, so users can see today's matter on each design before generating.
    /// Returns the preview image path (under wwwroot/posters/previews/).
    /// </summary>
    Task<string?> RenderPreviewAsync(
        DateTime date,
        IReadOnlyList<EventItem> events,
        string? customEvent,
        int? templateId,
        int tenantId,
        CancellationToken ct = default);
}

public class PosterGenerationService : IPosterGenerationService
{
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly ITextGenerationService _text;
    private readonly IPosterImageService _image;
    private readonly ILogger<PosterGenerationService> _logger;
    private readonly IActivityLog _log;

    public PosterGenerationService(
        IDbContextFactory<DailyPosterDbContext> dbFactory,
        ITextGenerationService text,
        IPosterImageService image,
        ILogger<PosterGenerationService> logger,
        IActivityLog log)
    {
        _dbFactory = dbFactory;
        _text = text;
        _image = image;
        _logger = logger;
        _log = log;
    }

    public async Task<GenerationResult> GenerateAsync(
        DateTime date,
        IReadOnlyList<EventItem> events,
        GenerateOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new GenerateOptions();

        var safeEvents = events.Count > 0 ? events : new List<EventItem>
        {
            new() { Text = $"{date:MMMM d} is a beautiful day to share something meaningful.", Kind = "event" }
        };

        var main = safeEvents.First();
        var category = ResolveCategory(main.Kind);
        var description = BuildDescription(safeEvents);
        return await GenerateCoreAsync(date, main, category, description, safeEvents.Take(6).ToList(), options, ct);
    }

    public async Task<GenerationResult> GenerateEventAsync(
        DateTime date,
        EventItem item,
        GenerateOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new GenerateOptions();

        var category = ResolveCategory(item.Kind);
        return await GenerateCoreAsync(date, item, category, string.Empty, new List<EventItem> { item }, options, ct);
    }

    private async Task<GenerationResult> GenerateCoreAsync(
        DateTime date,
        EventItem main,
        string category,
        string description,
        IReadOnlyList<EventItem> posterEvents,
        GenerateOptions options,
        CancellationToken ct)
    {
        var template = await ResolveTemplateAsync(options, ct);

        var poster = new Poster
        {
            TenantId = options.TenantId,
            TemplateId = template?.Id,
            TemplateName = template?.Name,
            Title = date.ToString("MMMM d, yyyy"),
            EventTitle = main.Text,
            Description = description,
            Category = category,
            EventDate = date.Date,
            Status = PosterStatus.Draft,
            Source = PosterSource.Automatic,
            CreatedAt = DateTime.UtcNow
        };

        foreach (var e in posterEvents)
        {
            poster.Events.Add(new PosterEvent
            {
                TenantId = options.TenantId,
                Text = e.Text,
                Year = e.Year,
                Kind = e.Kind,
                Url = e.Url
            });
        }

        if (options.Persist)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.Posters.Add(poster);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            var copy = await _text.GenerateAsync(poster, ct);
            poster.Caption = copy.Caption;
            poster.Hashtags = copy.Hashtags;
            poster.AiProvider = copy.Provider;

            poster.ImagePath = await _image.GenerateAsync(poster, template?.Theme, template?.AccentColor, template, logosOnly: options.LogosOnly, ct);

            poster.Status = PosterStatus.Ready;
            poster.GeneratedAt = DateTime.UtcNow;

            if (options.Persist)
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                db.Posters.Update(poster);
                await db.SaveChangesAsync(ct);
            }

            _logger.LogInformation("Generated poster #{Id} for {Date} using {Provider}", poster.Id, date.ToString("yyyy-MM-dd"), copy.Provider);
            _log.Add("generate", $"Generated poster #{poster.Id} for {date:dd MMM yyyy}: {poster.EventTitle}");
            return new GenerationResult(true, poster, null);
        }
        catch (Exception ex)
        {
            poster.Status = PosterStatus.Failed;
            poster.ErrorMessage = ex.Message;

            if (options.Persist)
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                db.Posters.Update(poster);
                await db.SaveChangesAsync(ct);
            }

            _logger.LogError(ex, "Poster generation failed for {Date}", date.ToString("yyyy-MM-dd"));
            _log.Add("error", $"Generation failed for {date:dd MMM yyyy}: {poster.EventTitle} ({ex.Message})");
            return new GenerationResult(false, poster, ex.Message);
        }
    }

    public async Task<BulkGenerationResult> GenerateAllTemplatesAsync(
        DateTime date,
        IReadOnlyList<EventItem> events,
        int tenantId,
        bool crossTenant = false,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var templates = await db.PosterTemplates.AsNoTracking()
            .Where(t => t.IsActive && (crossTenant || t.TenantId == tenantId || t.TenantId == 0))
            .OrderBy(t => t.TenantId == tenantId ? 0 : t.TenantId == 0 ? 1 : 2)
            .ThenBy(t => t.TenantId)
            .ThenBy(t => t.Id)
            .ToListAsync(ct);

        var existingTemplateIds = (await db.Posters.AsNoTracking()
                .Where(p => p.EventDate == date.Date && p.TenantId == tenantId && p.TemplateId != null)
                .Select(p => p.TemplateId!.Value)
                .ToListAsync(ct))
            .ToHashSet();

        var created = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var template in templates)
        {
            if (existingTemplateIds.Contains(template.Id))
            {
                skipped++;
                continue;
            }

            var result = await GenerateAsync(date, events,
                new GenerateOptions { Persist = true, TenantId = tenantId, TemplateId = template.Id }, ct);

            if (result.Success)
            {
                created++;
            }
            else
            {
                errors.Add($"{template.Name}: {result.Error}");
            }
        }

        _log.Add("generate", $"Bulk generation for {date:dd MMM yyyy}: {created} created, {skipped} skipped, {errors.Count} failed across {templates.Count} templates.");
        return new BulkGenerationResult(templates.Count, created, skipped, errors);
    }

    public async Task<string?> RenderPreviewAsync(
        DateTime date,
        IReadOnlyList<EventItem> events,
        string? customEvent,
        int? templateId,
        int tenantId,
        CancellationToken ct = default)
    {
        var safeEvents = events.Count > 0
            ? events
            : new List<EventItem>
            {
                new() { Text = $"{date:MMMM d} is a beautiful day to share something meaningful.", Kind = "event" }
            };

        var all = safeEvents.ToList();
        if (!string.IsNullOrWhiteSpace(customEvent))
        {
            all.Insert(0, new EventItem { Text = customEvent.Trim(), Kind = "event" });
        }

        if (all.Count == 0)
        {
            return null;
        }

        var main = all.First();
        var poster = new Poster
        {
            TenantId = tenantId,
            Title = date.ToString("MMMM d, yyyy"),
            EventTitle = main.Text,
            Description = BuildDescription(all),
            Category = ResolveCategory(main.Kind),
            EventDate = date.Date,
            Status = PosterStatus.Draft,
            Caption = BuildDescription(all),
            Hashtags = "#DailyPoster #OnThisDay",
            CreatedAt = DateTime.UtcNow
        };

        var template = await ResolveTemplateAsync(new GenerateOptions { TenantId = tenantId, TemplateId = templateId }, ct);
        poster.TemplateId = template?.Id;
        poster.TemplateName = template?.Name;

        try
        {
            return await _image.RenderPreviewAsync(poster, template?.Theme, template?.AccentColor, template, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preview render failed for {Date}", date.ToString("yyyy-MM-dd"));
            throw;
        }
    }

    private async Task<PosterTemplate?> ResolveTemplateAsync(GenerateOptions options, CancellationToken ct)
    {
        if (options.TemplateId is not null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.PosterTemplates.AsNoTracking()
                .Where(t => t.Id == options.TemplateId && t.IsActive)
                .FirstOrDefaultAsync(ct);
        }

        // No template chosen: prefer the tenant's own templates, then system
        // templates, then (for admins) any other workspace's templates.
        await using var db2 = await _dbFactory.CreateDbContextAsync(ct);
        var tenant = await db2.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == options.TenantId, ct);
        var sector = tenant?.Sector ?? Models.SectorCatalog.General;

        return await db2.PosterTemplates.AsNoTracking()
            .Where(t => t.IsActive && (options.CrossTenant || t.TenantId == options.TenantId || t.TenantId == 0))
            .OrderBy(t => t.TenantId == options.TenantId ? 0 : t.TenantId == 0 ? 1 : 2)
            .ThenBy(t => t.TenantId == 0 && t.Sector == sector ? 0 : 1)
            .ThenBy(t => t.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> HasPosterForAsync(DateTime date, int tenantId = 1)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Posters.AsNoTracking().AnyAsync(p => p.EventDate == date.Date && p.TenantId == tenantId);
    }

    public async Task<bool> HasPosterForEventAsync(DateTime date, string eventTitle, int tenantId = 1)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Posters.AsNoTracking().AnyAsync(p => p.EventDate == date.Date && p.TenantId == tenantId && p.EventTitle == eventTitle);
    }

    private static string ResolveCategory(string kind) => kind switch
    {
        "births" => "Birthday",
        "deaths" => "Memorial",
        "holidays" => "Celebration",
        "selected" => "History",
        "events" => "History",
        _ => "Today"
    };

    private static string BuildDescription(IReadOnlyList<EventItem> events)
    {
        var parts = events
            .Skip(1)
            .Take(3)
            .Select(e => e.Year.HasValue ? $"{e.Text} ({e.Year})." : e.Text + ".")
            .ToList();

        return parts.Count > 0 ? string.Join(" ", parts) : string.Empty;
    }
}
