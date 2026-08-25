using System.Diagnostics;
using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services;
using DailyPosterGenerator.Services.MultiTenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly IEventService _events;
    private readonly IPosterGenerationService _generation;
    private readonly TenantContext _tenant;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        IDbContextFactory<DailyPosterDbContext> dbFactory,
        IEventService events,
        IPosterGenerationService generation,
        TenantContext tenant,
        IWebHostEnvironment env,
        ILogger<HomeController> logger)
    {
        _dbFactory = dbFactory;
        _events = events;
        _generation = generation;
        _tenant = tenant;
        _env = env;
        _logger = logger;
    }

    public async Task<IActionResult> Index(int? month = null, int? year = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var today = DateTime.Today;
        var tenantId = _tenant.TenantId;
        var calMonth = month ?? today.Month;
        var calYear = year ?? today.Year;
        if (calMonth < 1) { calMonth = 1; }
        if (calMonth > 12) { calMonth = 12; }
        if (calYear < 1900) { calYear = 1900; }
        if (calYear > 2100) { calYear = 2100; }

        var calStart = new DateTime(calYear, calMonth, 1);
        var calEnd = calStart.AddMonths(1).AddDays(-1);
        var calendarPosterDates = await db.Posters.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.EventDate >= calStart && p.EventDate <= calEnd)
            .Select(p => p.EventDate)
            .ToListAsync(ct);

        var vm = new HomeViewModel
        {
            Today = today,
            CalendarYear = calYear,
            CalendarMonth = calMonth,
            CalendarPosterDates = calendarPosterDates,
            TodaysPoster = await db.Posters
                .Include(p => p.Events)
                .OrderByDescending(p => p.EventDate)
                .Where(p => p.TenantId == tenantId && p.EventDate == today)
                .FirstOrDefaultAsync(ct),
            RecentPosters = await db.Posters
                .Include(p => p.Events)
                .Where(p => p.TenantId == tenantId)
                .OrderByDescending(p => p.CreatedAt)
                .Take(6)
                .ToListAsync(ct),
            TotalCount = await db.Posters.CountAsync(p => p.TenantId == tenantId, ct),
            PublishedCount = await db.Posters.CountAsync(p => p.TenantId == tenantId && p.Status == PosterStatus.Published, ct),
            ReadyCount = await db.Posters.CountAsync(p => p.TenantId == tenantId && p.Status == PosterStatus.Ready, ct),
            FailedCount = await db.Posters.CountAsync(p => p.TenantId == tenantId && p.Status == PosterStatus.Failed, ct)
        };

        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> Events(DateTime? date, int? templateId, CancellationToken ct = default)
    {
        date = (date ?? DateTime.Today).Date;
        var items = await _events.GetTodaysEventsAsync(date.Value, ct);

        var vm = new EventPreviewViewModel
        {
            Date = date.Value,
            FromApi = items.Count > 0,
            Events = items.Take(10).Select(e => new TodayEventItem
            {
                Text = e.Text,
                Year = e.Year,
                Kind = e.Kind,
                Url = e.Url,
                Selected = true
            }).ToList(),
            Templates = await GetAvailableTemplatesAsync(ct),
            SelectedTemplateId = templateId
        };

        ViewBag.CurrentTenantId = _tenant.TenantId;
        ViewBag.TenantNames = _tenant.IsAdmin ? await GetTenantNamesAsync(ct) : new Dictionary<int, string>();

        return View(vm);
    }

    /// <summary>
    /// JSON feed powering the Generate page's live date switch: returns the events
    /// for the requested day so the checklist can refresh without a full reload.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> EventsList(DateTime? date, CancellationToken ct = default)
    {
        date = (date ?? DateTime.Today).Date;
        var items = await _events.GetTodaysEventsAsync(date.Value, ct);

        return Json(new
        {
            date = date.Value.ToString("yyyy-MM-dd"),
            pretty = date.Value.ToString("dddd, MMMM d, yyyy"),
            fromApi = items.Count > 0,
            events = items.Take(10).Select(e => new { e.Text, e.Year, e.Kind, e.Url })
        });
    }

    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Preview(DateTime? date, int? templateId, string? events, string? customEvent, CancellationToken ct = default)
    {
        date = (date ?? DateTime.Today).Date;
        var all = await _events.GetTodaysEventsAsync(date.Value, ct);

        var chosen = new List<EventItem>();
        var selected = new HashSet<int>();
        if (!string.IsNullOrWhiteSpace(events))
        {
            foreach (var part in events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out var i) && i >= 0 && i < all.Count)
                {
                    selected.Add(i);
                }
            }
        }

        for (var i = 0; i < all.Count; i++)
        {
            if (selected.Contains(i))
            {
                chosen.Add(all[i]);
            }
        }

        var path = await _generation.RenderPreviewAsync(date.Value, chosen, customEvent, templateId, _tenant.TenantId, ct);
        if (string.IsNullOrWhiteSpace(path))
        {
            return NotFound();
        }

        var wwwRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var full = Path.Combine(wwwRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(full))
        {
            return NotFound();
        }

        return File(System.IO.File.OpenRead(full), "image/png");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(IReadOnlyList<int>? selectedEvents, string? customEvent, DateTime? date, int? templateId, CancellationToken ct = default)
    {
        date = (date ?? DateTime.Today).Date;
        var all = await _events.GetTodaysEventsAsync(date.Value, ct);

        var chosen = new List<EventItem>();
        var selected = selectedEvents?.ToHashSet() ?? new HashSet<int>();
        for (var i = 0; i < all.Count; i++)
        {
            if (selected.Contains(i))
            {
                chosen.Add(all[i]);
            }
        }

        if (!string.IsNullOrWhiteSpace(customEvent))
        {
            chosen.Insert(0, new EventItem { Text = customEvent.Trim(), Kind = "event" });
        }

        if (chosen.Count == 0)
        {
            TempData["Notice"] = "Select at least one event to generate a poster.";
            return RedirectToAction(nameof(Events));
        }

        var result = await _generation.GenerateAsync(date.Value, chosen,
            new GenerateOptions { Persist = true, TenantId = _tenant.TenantId, TemplateId = templateId, CrossTenant = _tenant.IsAdmin }, ct);

        if (!result.Success || result.Poster is null)
        {
            TempData["Error"] = $"Poster generation failed: {result.Error}";
            return RedirectToAction(nameof(Events));
        }

        TempData["Success"] = $"Poster generated in {result.Poster.CreatedAt:HH:mm:ss}.";
        return RedirectToAction("Details", "Posters", new { id = result.Poster.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateAll(string? customEvent, DateTime? date, CancellationToken ct = default)
    {
        date = (date ?? DateTime.Today).Date;

        var events = await _events.GetTodaysEventsAsync(date.Value, ct);
        var chosen = events.ToList();
        if (!string.IsNullOrWhiteSpace(customEvent))
        {
            chosen.Insert(0, new EventItem { Text = customEvent.Trim(), Kind = "event" });
        }

        var result = await _generation.GenerateAllTemplatesAsync(date.Value, chosen, _tenant.TenantId, _tenant.IsAdmin, ct);

        if (result.Created == 0)
        {
            TempData["Notice"] = result.Errors.Count > 0
                ? $"Bulk generation failed. First errors: {string.Join("; ", result.Errors.Take(3))}"
                : $"Nothing to generate â€” every template already has a poster for {date.Value:dd MMM yyyy}.";
            return RedirectToAction(nameof(Events), new { date = date.Value.ToString("yyyy-MM-dd") });
        }

        var message = $"Created {result.Created} poster(s) across all templates";
        if (result.Skipped > 0)
        {
            message += $" ({result.Skipped} already existed)";
        }

        TempData["Success"] = message + ".";
        return RedirectToAction("Index", "Posters");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateTodayAuto(CancellationToken ct = default)
    {
        var date = DateTime.Today;
        var tenantId = _tenant.TenantId;

        if (await _generation.HasPosterForAsync(date, tenantId))
        {
            var existing = await GetTodaysPosterAsync(date, tenantId, ct);
            if (existing is not null)
            {
                TempData["Notice"] = "Today's poster already exists. Open it below to download or publish.";
                return RedirectToAction("Details", "Posters", new { id = existing.Id });
            }
        }

        var events = await _events.GetTodaysEventsAsync(date, ct);
        var result = await _generation.GenerateAsync(date, events,
            new GenerateOptions { Persist = true, TenantId = tenantId, CrossTenant = _tenant.IsAdmin }, ct);

        if (!result.Success || result.Poster is null)
        {
            TempData["Error"] = $"Poster generation failed: {result.Error}";
            return RedirectToAction(nameof(Index));
        }

        TempData["Success"] = "Today's poster is ready!";
        return RedirectToAction("Details", "Posters", new { id = result.Poster.Id });
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [AllowAnonymous]
    public IActionResult Error()
    {
        return View(new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
    }

    private async Task<List<PosterTemplate>> GetAvailableTemplatesAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.PosterTemplates.AsNoTracking()
            .Where(t => t.IsActive && (_tenant.IsAdmin || t.TenantId == _tenant.TenantId || t.TenantId == 0))
            .OrderBy(t => t.TenantId == _tenant.TenantId ? 0 : t.TenantId == 0 ? 1 : 2)
            .ThenBy(t => t.TenantId)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);
    }

    private async Task<Dictionary<int, string>> GetTenantNamesAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Tenants.AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
    }

    private async Task<Poster?> GetTodaysPosterAsync(DateTime date, int tenantId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Posters
            .Include(p => p.Events)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EventDate == date.Date, ct);
    }
}