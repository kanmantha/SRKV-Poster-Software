using System.Text.RegularExpressions;
using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services;
using DailyPosterGenerator.Services.MultiTenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Controllers;

[Authorize]
public class StudioController : Controller
{
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly IStudioPromptService _studio;
    private readonly IPosterGenerationService _generation;
    private readonly IEventService _events;
    private readonly TenantContext _tenant;
    private readonly ILogger<StudioController> _logger;

    public StudioController(
        IDbContextFactory<DailyPosterDbContext> dbFactory,
        IStudioPromptService studio,
        IPosterGenerationService generation,
        IEventService events,
        TenantContext tenant,
        ILogger<StudioController> logger)
    {
        _dbFactory = dbFactory;
        _studio = studio;
        _generation = generation;
        _events = events;
        _tenant = tenant;
        _logger = logger;
    }

    public IActionResult Index(DateTime? date)
    {
        if (date.HasValue)
        {
            ViewBag.InitialDate = date.Value.Date.ToString("yyyy-MM-dd");
        }

        return View();
    }

    /// <summary>
    /// Returns short occasion-prompt suggestions that match the given poster date,
    /// so the Studio's suggestion chips reflect what is actually being celebrated.
    /// </summary>
    [HttpGet]
    public IActionResult Occasions(DateTime date)
    {
        return Json(BuildOccasionSuggestions(date.Date));
    }

    private static List<string> BuildOccasionSuggestions(DateTime date)
    {
        var events = OfflineEventCalendar.GetEvents(date, 8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<string>();

        foreach (var ev in events)
        {
            if (ev.Kind != "holiday") continue;
            var label = ShortOccasionLabel(ev.Text);
            if (label is not null && seen.Add(label))
            {
                suggestions.Add(label);
            }

            if (suggestions.Count >= 6)
            {
                break;
            }
        }

        // Quiet dates still deserve a couple of usable prompt ideas.
        if (suggestions.Count < 3)
        {
            foreach (var extra in new[]
            {
                $"{date:MMM d} poster",
                $"{date.DayOfWeek} special",
                "Today's offer"
            })
            {
                if (seen.Add(extra))
                {
                    suggestions.Add(extra);
                }
            }
        }

        return suggestions.Take(6).ToList();
    }

    private static string? ShortOccasionLabel(string text)
    {
        text = Regex.Replace(text, @"^\d{3,4}:\s*", "").Trim();
        var head = text.Split(',')[0].Trim().TrimEnd('.').Trim();
        return head.Length is > 0 and <= 40 ? head : null;
    }

    /// <summary>
    /// Creates a new style-variant template for the prompt and renders a preview poster
    /// on it, featuring the events of the selected calendar date. Called once for the
    /// first design and again for every Regenerate click.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string? prompt, int round = 1, DateTime? date = null, bool dateExplicit = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return BadRequest(new { ok = false, error = "Describe what the poster is about." });
        }

        try
        {
            var plan = await BuildPlanAsync(prompt, ct);

            // When the user has not chosen a date themselves, an occasion prompt
            // (e.g. "Women's Day") steers the poster to that occasion's next date.
            var when = dateExplicit || plan.SuggestedDate is null
                ? (date ?? DateTime.Today).Date
                : plan.SuggestedDate.Value.Date;
            var dateAutoSet = !dateExplicit && plan.SuggestedDate is not null && when != (date ?? DateTime.Today).Date;

            var template = await _studio.CreateVariantAsync(plan, round, _tenant.TenantId, ct);

            var dayEvents = await _events.GetTodaysEventsAsync(when, ct);
            var previewPath = await _generation.RenderPreviewAsync(
                when,
                dayEvents.Take(6).ToList(),
                plan.Title,
                template.Id,
                _tenant.TenantId,
                ct);

            if (string.IsNullOrWhiteSpace(previewPath))
            {
                return StatusCode(500, new { ok = false, error = "Could not render the poster preview." });
            }

            return Json(new
            {
                ok = true,
                round,
                templateId = template.Id,
                templateName = template.Name,
                theme = template.Theme,
                accent = template.AccentColor,
                sectorLabel = SectorCatalog.Label(plan.Sector),
                title = plan.Title,
                previewUrl = previewPath,
                eventDate = when.ToString("yyyy-MM-dd"),
                dateAutoSet,
                eventCount = dayEvents.Count,
                featured = dayEvents.Take(3).Select(e => e.Text).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Studio create failed for prompt '{Prompt}' (round {Round}).", prompt, round);
            return StatusCode(500, new { ok = false, error = $"Something went wrong while designing your poster: {ex.Message}" });
        }
    }

    /// <summary>Saves the approved design as a real poster in the gallery.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string? prompt, int? templateId, DateTime? date = null, bool dateExplicit = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) || templateId is null)
        {
            TempData["Error"] = "Pick a design before saving.";
            return RedirectToAction(nameof(Index));
        }

        var plan = await BuildPlanAsync(prompt, ct);
        var when = dateExplicit || plan.SuggestedDate is null
            ? (date ?? DateTime.Today).Date
            : plan.SuggestedDate.Value.Date;

        var dayEvents = await _events.GetTodaysEventsAsync(when, ct);

        var allEvents = new List<EventItem> { new() { Text = plan.Title, Kind = "event" } };
        allEvents.AddRange(dayEvents.Take(6));

        var result = await _generation.GenerateAsync(
            when,
            allEvents,
            new GenerateOptions { Persist = true, TenantId = _tenant.TenantId, TemplateId = templateId },
            ct);

        if (!result.Success || result.Poster is null)
        {
            TempData["Error"] = $"Could not save the poster: {result.Error}";
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction("Details", "Posters", new { id = result.Poster.Id });
    }

    private async Task<StudioPlan> BuildPlanAsync(string prompt, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tenantSector = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == _tenant.TenantId)
            .Select(t => t.Sector)
            .FirstOrDefaultAsync(ct);

        return _studio.Parse(prompt, tenantSector ?? SectorCatalog.General);
    }
}
