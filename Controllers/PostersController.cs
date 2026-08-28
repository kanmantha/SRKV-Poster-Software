using System.IO.Compression;
using System.Text;
using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services;
using DailyPosterGenerator.Services.MultiTenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Controllers;

[Authorize]
public class PostersController : Controller
{
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly IPosterGenerationService _generation;
    private readonly IPublishService _publish;
    private readonly IEventService _events;
    private readonly IWebHostEnvironment _env;
    private readonly TenantContext _tenant;

    public PostersController(
        IDbContextFactory<DailyPosterDbContext> dbFactory,
        IPosterGenerationService generation,
        IPublishService publish,
        IEventService events,
        IWebHostEnvironment env,
        TenantContext tenant)
    {
        _dbFactory = dbFactory;
        _generation = generation;
        _publish = publish;
        _events = events;
        _env = env;
        _tenant = tenant;
    }

    public async Task<IActionResult> Index(PosterStatus? status = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.Posters.Include(p => p.Events).AsNoTracking().Where(p => p.TenantId == _tenant.TenantId);
        if (status.HasValue)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        var posters = await query.OrderByDescending(p => p.CreatedAt).Take(60).ToListAsync(ct);

        ViewBag.StatusFilter = status;
        return View(posters);
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var poster = await db.Posters
            .Include(p => p.Events)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == _tenant.TenantId, ct);

        if (poster is null)
        {
            return NotFound();
        }

        var platforms = await db.Platforms.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);

        return View(new PosterDetailsViewModel { Poster = poster, EnabledPlatforms = platforms });
    }

    public async Task<IActionResult> Image(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var poster = await db.Posters.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.TenantId == _tenant.TenantId, ct);
        if (poster is null || string.IsNullOrWhiteSpace(poster.ImagePath))
        {
            return NotFound();
        }

        var fullPath = MapImagePath(poster.ImagePath);
        if (poster.ImageBytes is { Length: > 0 })
        {
            return File(poster.ImageBytes, "image/png");
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        return File(System.IO.File.OpenRead(fullPath), "image/png");
    }

    public async Task<IActionResult> DownloadImage(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var poster = await db.Posters.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.TenantId == _tenant.TenantId, ct);
        if (poster is null)
        {
            return NotFound();
        }

        var fileName = BuildFileName(poster.EventTitle, poster.EventDate);
        if (poster.ImageBytes is { Length: > 0 })
        {
            return File(poster.ImageBytes, "image/png", fileName);
        }

        var fullPath = MapImagePath(poster.ImagePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(poster.ImagePath) || !System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        return PhysicalFile(fullPath, "image/png", fileName);
    }

    public async Task<IActionResult> DownloadCaption(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var poster = await db.Posters.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.TenantId == _tenant.TenantId, ct);
        if (poster is null)
        {
            return NotFound();
        }

        var content = $"{poster.Caption}\n\n{poster.Hashtags}\n";
        var fileName = BuildFileName(poster.EventTitle, poster.EventDate).Replace(".png", ".txt");
        return File(System.Text.Encoding.UTF8.GetBytes(content), "text/plain", fileName);
    }

    public async Task<IActionResult> DownloadDay(DateTime date, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var posters = await db.Posters.AsNoTracking()
            .Where(p => p.TenantId == _tenant.TenantId && p.EventDate == date.Date)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        if (posters.Count == 0)
        {
            TempData["Notice"] = $"No posters exist for {date:dd MMM yyyy}. Generate one first.";
            return RedirectToAction("Events", "Home", new { date = date.ToString("yyyy-MM-dd") });
        }

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var sb = new StringBuilder();
            foreach (var poster in posters)
            {
                var fileName = BuildFileName(poster.EventTitle, poster.EventDate);
                var fullPath = MapImagePath(poster.ImagePath ?? string.Empty);
                if (System.IO.File.Exists(fullPath))
                {
                    var entry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    using var fs = System.IO.File.OpenRead(fullPath);
                    await fs.CopyToAsync(es, ct);

                    sb.AppendLine($"{fileName} — {poster.EventTitle}");
                    sb.AppendLine($"  Caption: {poster.Caption}");
                    sb.AppendLine($"  Hashtags: {poster.Hashtags}");
                    sb.AppendLine();
                }
            }

            var captions = archive.CreateEntry("captions.txt", CompressionLevel.Optimal);
            using (var cs = captions.Open())
            using (var sw = new StreamWriter(cs, Encoding.UTF8))
            {
                await sw.WriteAsync(sb.ToString());
            }
        }

        return File(ms.ToArray(), "application/zip", $"posters-{date:yyyy-MM-dd}.zip");
    }

    public async Task<IActionResult> Tomorrow(CancellationToken ct = default)
    {
        var date = DateTime.Today.AddDays(1).Date;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var posters = await db.Posters
            .Include(p => p.Events)
            .AsNoTracking()
            .Where(p => p.TenantId == _tenant.TenantId && p.EventDate == date)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        ViewBag.Date = date;
        return View(posters);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var poster = await db.Posters.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == _tenant.TenantId, ct);
        if (poster is null)
        {
            return NotFound();
        }

        var result = await _publish.PublishAsync(poster, ct);
        if (result.Success)
        {
            TempData["Success"] = result.Platforms.Count > 0
                ? $"Published to: {string.Join(", ", result.Platforms)}."
                : "Marked as published. Configure webhook URLs in Settings to push content automatically.";
        }
        else
        {
            TempData["Error"] = $"Publish failed: {result.Error}";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Regenerate(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var poster = await db.Posters
            .Include(p => p.Events)
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == _tenant.TenantId, ct);

        if (poster is null)
        {
            return NotFound();
        }

        var events = poster.Events
            .OrderBy(e => e.Kind == "selected" ? 0 : 1)
            .Select(e => new EventItem { Text = e.Text, Year = e.Year, Kind = e.Kind, Url = e.Url })
            .ToList();

        var result = await _generation.GenerateAsync(poster.EventDate, events,
            new GenerateOptions { Persist = true, TenantId = poster.TenantId, TemplateId = poster.TemplateId }, ct);

        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? "Poster regenerated successfully."
            : $"Regeneration failed: {result.Error}";

        return RedirectToAction(nameof(Details), new { id = result.Poster?.Id ?? id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateKeepLogos(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var poster = await db.Posters
            .Include(p => p.Events)
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == _tenant.TenantId, ct);

        if (poster is null)
        {
            return NotFound();
        }

        var events = poster.Events
            .OrderBy(e => e.Kind == "selected" ? 0 : 1)
            .Select(e => new EventItem { Text = e.Text, Year = e.Year, Kind = e.Kind, Url = e.Url })
            .ToList();

        var result = await _generation.GenerateAsync(poster.EventDate, events,
            new GenerateOptions { Persist = true, TenantId = poster.TenantId, TemplateId = poster.TemplateId, LogosOnly = true }, ct);

        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? "Poster regenerated with logos only (text removed)."
            : $"Regeneration failed: {result.Error}";

        return RedirectToAction(nameof(Details), new { id = result.Poster?.Id ?? id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var poster = await db.Posters.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == _tenant.TenantId, ct);
        if (poster is not null)
        {
            DeleteImageFile(poster.ImagePath);
            db.Posters.Remove(poster);
            await db.SaveChangesAsync(ct);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSelected(int[]? ids, CancellationToken ct = default)
    {
        if (ids is null || ids.Length == 0)
        {
            TempData["Notice"] = "No posters were selected.";
            return RedirectToAction(nameof(Index));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var posters = await db.Posters.Where(p => p.TenantId == _tenant.TenantId && ids.Contains(p.Id)).ToListAsync(ct);

        foreach (var poster in posters)
        {
            DeleteImageFile(poster.ImagePath);
        }

        db.Posters.RemoveRange(posters);
        await db.SaveChangesAsync(ct);

        TempData["Success"] = $"Deleted {posters.Count} selected poster{(posters.Count == 1 ? "" : "s")}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAll(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var posters = await db.Posters.Where(p => p.TenantId == _tenant.TenantId).ToListAsync(ct);

        foreach (var poster in posters)
        {
            DeleteImageFile(poster.ImagePath);
        }

        db.Posters.RemoveRange(posters);
        await db.SaveChangesAsync(ct);

        TempData["Success"] = $"Deleted all {posters.Count} poster{(posters.Count == 1 ? "" : "s")}.";
        return RedirectToAction(nameof(Index));
    }

    private void DeleteImageFile(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        try
        {
            var fullPath = MapImagePath(imagePath);
            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }
        }
        catch (Exception)
        {
            // ignore cleanup failures
        }
    }

    private string BuildFileName(string title, DateTime date)
    {
        var safe = string.Concat(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        if (safe.Length > 60)
        {
            safe = safe[..60].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(safe)
            ? $"poster-{date:yyyy-MM-dd}.png"
            : $"{safe} - {date:yyyy-MM-dd}.png";
    }

    private string MapImagePath(string imagePath)
    {
        var wwwRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var relative = imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(wwwRoot, relative);
    }
}