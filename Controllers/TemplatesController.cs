using System.Text.Json;
using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services;
using DailyPosterGenerator.Services.MultiTenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Controllers;

[Authorize]
public class TemplatesController : Controller
{
    private static readonly string[] ThemeOptions =
        new[] { "srv", "colorful", "light", "dark", "auto" }
            .Concat(PosterTheme.SignatureModes)
            .ToArray();

private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly TenantContext _tenant;
    private readonly ITemplateThumbnailService _thumbnails;
    private readonly ITemplateImportService _import;
    private readonly IWebHostEnvironment _env;

    public TemplatesController(
        IDbContextFactory<DailyPosterDbContext> dbFactory,
        TenantContext tenant,
        ITemplateThumbnailService thumbnails,
        ITemplateImportService import,
        IWebHostEnvironment env)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _thumbnails = thumbnails;
        _import = import;
        _env = env;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var templates = await db.PosterTemplates.AsNoTracking()
            .Where(t => _tenant.IsAdmin || t.TenantId == _tenant.TenantId || t.TenantId == 0)
            .OrderBy(t => t.TenantId == _tenant.TenantId ? 0 : t.TenantId == 0 ? 1 : 2)
            .ThenBy(t => t.TenantId)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);

        ViewBag.CurrentTenantId = _tenant.TenantId;
        ViewBag.IsAdmin = _tenant.IsAdmin;
        ViewBag.TenantNames = await db.Tenants.AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

var changed = false;
        foreach (var t in templates)
        {
            if (string.IsNullOrWhiteSpace(t.ThumbnailPath) || t.ThumbnailBytes is not { Length: > 0 })
            {
                var path = await _thumbnails.EnsureThumbnailAsync(t, ct);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    t.ThumbnailPath = path;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            await using var dbw = await _dbFactory.CreateDbContextAsync(ct);
            foreach (var t in templates.Where(t => t.Id > 0 && !string.IsNullOrWhiteSpace(t.ThumbnailPath)))
            {
                var entity = await dbw.PosterTemplates.FindAsync([t.Id], ct);
                if (entity is null)
                {
                    continue;
                }

                var dirty = false;
                if (entity.ThumbnailPath != t.ThumbnailPath)
                {
                    entity.ThumbnailPath = t.ThumbnailPath;
                    dirty = true;
                }

                if (entity.ThumbnailBytes is not { Length: > 0 } && t.ThumbnailBytes is { Length: > 0 })
                {
                    entity.ThumbnailBytes = t.ThumbnailBytes;
                    dirty = true;
                }

                if (dirty)
                {
                    dbw.PosterTemplates.Update(entity);
                }
            }

            await dbw.SaveChangesAsync(ct);
        }

        return View(templates);
    }

    /// <summary>Serves a template thumbnail from the database (regenerating and persisting
    /// on demand when it is missing), so the template gallery survives Render's ephemeral
    /// disk being wiped on every deploy/restart.</summary>
    [HttpGet]
    public async Task<IActionResult> Thumbnail(int id, CancellationToken ct)
    {
        _ = id;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var template = await db.PosterTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id
                && (_tenant.IsAdmin || t.TenantId == _tenant.TenantId || t.TenantId == 0), ct);

        if (template is null)
        {
            return NotFound();
        }

        if (template.ThumbnailBytes is not { Length: > 0 })
        {
            var path = await _thumbnails.EnsureThumbnailAsync(template, ct);
            if (string.IsNullOrWhiteSpace(path) || template.ThumbnailBytes is not { Length: > 0 })
            {
                return NotFound();
            }

            var entity = await db.PosterTemplates.FindAsync([id], ct);
            if (entity is not null)
            {
                entity.ThumbnailBytes = template.ThumbnailBytes;
                if (string.IsNullOrWhiteSpace(entity.ThumbnailPath))
                {
                    entity.ThumbnailPath = path;
                }

                await db.SaveChangesAsync(ct);
            }
        }

        return File(template.ThumbnailBytes!, "image/png");
    }

    public IActionResult Use(int id)
    {
        return RedirectToAction("Events", "Home", new { templateId = id });
    }

    [HttpGet]
    public IActionResult Create()
    {
        ViewBag.ThemeOptions = ThemeOptions;
        return View(new PosterTemplate { Theme = "colorful", IsActive = true });
    }

    [HttpGet]
    public IActionResult Import()
    {
        return View(new TemplateImportViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Import(TemplateImportViewModel model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            ModelState.AddModelError(nameof(TemplateImportViewModel.Name), "Give the template a name.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        if (model.Upload is null || model.Upload.Length == 0)
        {
            ModelState.AddModelError(nameof(TemplateImportViewModel.Upload), "Choose an image file to upload.");
            return View(model);
        }

        // The database enforces a unique template name per tenant; check up front so the user
        // gets a friendly message instead of an unhandled DbUpdateException.
        var requestedName = model.Name.Trim();
        await using (var dbCheck = await _dbFactory.CreateDbContextAsync(ct))
        {
            var nameTaken = await dbCheck.PosterTemplates
                .AnyAsync(t => t.TenantId == _tenant.TenantId && t.Name == requestedName, ct);
            if (nameTaken)
            {
                ModelState.AddModelError(
                    nameof(TemplateImportViewModel.Name),
                    $"A template named '{requestedName}' already exists. Choose a different name or delete the old one first.");
                return View(model);
            }
        }

        var treatmentRequest = TemplateImportService.NormalizeTreatment(new PosterTreatmentRequest(
            model.TreatmentKind ?? "original",
            model.TintHex,
            model.TintStrength ?? 0.4f,
            model.FreshTheme,
            model.FreshAccent));

        var result = await _import.ImportAsync(
            _tenant.TenantId,
            model.Name,
            model.Description,
            SectorCatalog.Normalize(model.Sector) ?? SectorCatalog.General,
            model.Upload,
            ParseBoxes(model.BoxesJson),
            treatmentRequest,
            ct);

        if (!result.Success || result.Template is null)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Import failed.");
            ModelState.AddModelError(string.Empty, result.Error ?? "Import failed.");
            return View(model);
        }

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            // Template names are unique per workspace; re-importing a poster with the
            // same name gets a numbered variant instead of a database error.
            result.Template.Name = await UniqueTemplateName(db, _tenant.TenantId, result.Template.Name, null, ct);
            db.PosterTemplates.Add(result.Template);
            await db.SaveChangesAsync(ct);
        }

        var path = await _thumbnails.EnsureThumbnailAsync(result.Template, ct);
        if (!string.IsNullOrWhiteSpace(path))
        {
            await using var db2 = await _dbFactory.CreateDbContextAsync(ct);
            var entity = await db2.PosterTemplates.FindAsync([result.Template.Id], ct);
            if (entity is not null)
            {
                entity.ThumbnailPath = path;
                await db2.SaveChangesAsync(ct);
            }
        }

        var refreshNote = treatmentRequest.Kind switch
        {
            "tint" => $" Refreshed with a {treatmentRequest.TintHex} colour wash.",
            "enhance" => " Colours brightened and enhanced.",
            "grayscale" => " Converted to black & white.",
            "fresh" => $" Rebuilt on a fresh {treatmentRequest.FreshTheme} background; your original upload is kept safe.",
            _ => string.Empty
        };
        TempData["Success"] = $"Template '{result.Template.Name}' created from your upload.{refreshNote} Pick it on the Events page to reuse the layout.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AiUpdate(IFormFile? upload, string? instruction, CancellationToken ct)
    {
        var result = await _import.ApplyInstructionAsync(upload, instruction, ct);
        if (!result.Success)
        {
            return Json(new { ok = false, message = result.Summary });
        }

        return Json(new
        {
            ok = true,
            message = result.Summary,
            image = result.Image is null ? null : "data:image/jpeg;base64," + Convert.ToBase64String(result.Image),
            boxes = result.Boxes.Select(b => new { type = b.Type, x = b.X, y = b.Y, w = b.W, h = b.H }),
            treatment = new
            {
                kind = result.Treatment.Kind,
                tintHex = result.Treatment.TintHex,
                tintStrength = result.Treatment.TintStrength,
                freshTheme = result.Treatment.FreshTheme,
                freshAccent = result.Treatment.FreshAccent
            }
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AiUpdateTemplate(int id, string? instruction, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var template = await db.PosterTemplates
            .FirstOrDefaultAsync(t => t.Id == id && !t.IsSystem
                && (t.TenantId == _tenant.TenantId || _tenant.IsAdmin), ct);

        if (template is null)
        {
            return Json(new { ok = false, message = "Template not found." });
        }

        var result = await _import.ApplyInstructionToTemplateAsync(template, instruction, ct);
        if (!result.Success)
        {
            return Json(new { ok = false, message = result.Summary });
        }

        await db.SaveChangesAsync(ct);

        var thumbPath = await _thumbnails.EnsureThumbnailAsync(template, ct);
        if (!string.IsNullOrWhiteSpace(thumbPath) && thumbPath != template.ThumbnailPath)
        {
            template.ThumbnailPath = thumbPath;
            await db.SaveChangesAsync(ct);
        }

        return Json(new
        {
            ok = true,
            message = result.Summary,
            image = result.Image is null ? null : "data:image/jpeg;base64," + Convert.ToBase64String(result.Image),
            backgroundUrl = template.IsImported ? template.BackgroundImagePath + "?v=" + DateTime.UtcNow.Ticks : null,
thumbUrl = !template.IsImported && !string.IsNullOrWhiteSpace(template.ThumbnailPath)
                ? Url.Action(nameof(Thumbnail), "Templates", new { id = template.Id }) + "?v=" + DateTime.UtcNow.Ticks
                : null,
            boxes = result.Boxes.Select(b => new { type = b.Type, x = b.X, y = b.Y, w = b.W, h = b.H }),
            treatment = new { kind = result.Treatment.Kind }
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewTreatment(
        IFormFile? upload,
        string? kind,
        string? tintHex,
        float? strength,
        string? freshTheme,
        string? freshAccent,
        CancellationToken ct)
    {
        var bytes = await _import.RenderTreatmentPreviewAsync(
            upload,
            TemplateImportService.NormalizeTreatment(new PosterTreatmentRequest(
                kind ?? "original", tintHex, strength ?? 0.4f, freshTheme, freshAccent)),
            ct);

        if (bytes is null)
        {
            return BadRequest();
        }

        return File(bytes, "image/jpeg");
    }

    [HttpPost]
    public async Task<IActionResult> AutoDetect(IFormFile? upload, CancellationToken ct = default)
    {
        if (upload is null || upload.Length == 0)
        {
            return Json(new { text = Array.Empty<ImportBox>(), logos = Array.Empty<ImportBox>() });
        }

        var detected = await _import.DetectRegionsAsync(upload, ct);
        return Json(new { text = detected.TextBoxes, logos = detected.LogoBoxes });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AutoDetectTemplate(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var template = await db.PosterTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && !t.IsSystem
                && (t.TenantId == _tenant.TenantId || _tenant.IsAdmin), ct);

        // Legacy imports have no stored original; detect on the processed background.
        var sourcePath = template is null
            ? null
            : (string.IsNullOrWhiteSpace(template.OriginalBackgroundPath)
                ? template.BackgroundImagePath
                : template.OriginalBackgroundPath);

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Json(new { text = Array.Empty<ImportBox>(), logos = Array.Empty<ImportBox>() });
        }

        var detected = await _import.DetectRegionsFromFileAsync(sourcePath, ct);
        return Json(new { text = detected.TextBoxes, logos = detected.LogoBoxes });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PosterTemplate model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.ThemeOptions = ThemeOptions;
            return View(model);
        }

        model.TenantId = _tenant.TenantId;
        model.IsSystem = false;
        model.UpdatedAt = DateTime.UtcNow;
model.Theme = string.IsNullOrWhiteSpace(model.Theme) ? "colorful" : model.Theme.Trim().ToLowerInvariant();
        model.Sector = SectorCatalog.Normalize(model.Sector);
        model.LogoPosition = string.IsNullOrWhiteSpace(model.LogoPosition) ? "top-right" : model.LogoPosition.Trim().ToLowerInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.PosterTemplates.AnyAsync(t => t.TenantId == model.TenantId && t.Name == model.Name, ct))
        {
            ModelState.AddModelError(nameof(PosterTemplate.Name), "A template with this name already exists.");
            ViewBag.ThemeOptions = ThemeOptions;
            return View(model);
        }

        db.PosterTemplates.Add(model);
        await db.SaveChangesAsync(ct);

        var path = await _thumbnails.EnsureThumbnailAsync(model, ct);
        if (!string.IsNullOrWhiteSpace(path))
        {
            model.ThumbnailPath = path;
            db.PosterTemplates.Update(model);
            await db.SaveChangesAsync(ct);
        }

        TempData["Success"] = $"Template '{model.Name}' created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var template = await db.PosterTemplates
            .FirstOrDefaultAsync(t => t.Id == id && !t.IsSystem
                && (t.TenantId == _tenant.TenantId || _tenant.IsAdmin), ct);

        if (template is null)
        {
            return NotFound();
        }

        ViewBag.ThemeOptions = ThemeOptions;
        ViewBag.ImportBoxesJson = template.ImportBoxesJson;
        return View(template);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, PosterTemplate model, string? editorBoxesJson, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var template = await db.PosterTemplates
            .FirstOrDefaultAsync(t => t.Id == id && !t.IsSystem
                && (t.TenantId == _tenant.TenantId || _tenant.IsAdmin), ct);

        if (template is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            ViewBag.ThemeOptions = ThemeOptions;
            ViewBag.ImportBoxesJson = template.ImportBoxesJson;
            return View(model);
        }

        var duplicate = await db.PosterTemplates.AnyAsync(
            t => t.Id != id && t.TenantId == template.TenantId && t.Name == model.Name, ct);
        if (duplicate)
        {
            ModelState.AddModelError(nameof(PosterTemplate.Name), "A template with this name already exists.");
            ViewBag.ThemeOptions = ThemeOptions;
            ViewBag.ImportBoxesJson = template.ImportBoxesJson;
            return View(model);
        }

        template.Name = model.Name.Trim();
        template.Description = model.Description?.Trim();
        template.Theme = string.IsNullOrWhiteSpace(model.Theme) ? "colorful" : model.Theme.Trim().ToLowerInvariant();
        template.AccentColor = model.AccentColor?.Trim();
        template.Sector = SectorCatalog.Normalize(model.Sector);
        if (template.IsImported)
        {
            template.TextColor = model.TextColor?.Trim();
            template.BackgroundDim = Math.Clamp(model.BackgroundDim, 0, 90);
        }

        template.LogoPosition = string.IsNullOrWhiteSpace(model.LogoPosition) ? "top-right" : model.LogoPosition.Trim().ToLowerInvariant();
        template.IsActive = model.IsActive;
        template.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        // Re-apply the poster editor's erase / erase-logo / keep boxes to the original upload
        // (legacy templates fall back to their processed background inside ReprocessAsync).
        var hasPosterSource = !string.IsNullOrWhiteSpace(template.OriginalBackgroundPath)
            || !string.IsNullOrWhiteSpace(template.BackgroundImagePath);
        if (!string.IsNullOrWhiteSpace(editorBoxesJson) && hasPosterSource)
        {
            var reprocess = await _import.ReprocessAsync(template, ParseBoxes(editorBoxesJson), ct);
            if (!reprocess.Success)
            {
                TempData["Error"] = reprocess.Error ?? "Could not update the poster layout.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            await db.SaveChangesAsync(ct);
        }

        var path = await _thumbnails.EnsureThumbnailAsync(template, ct);
        if (!string.IsNullOrWhiteSpace(path) && path != template.ThumbnailPath)
        {
            template.ThumbnailPath = path;
            await db.SaveChangesAsync(ct);
        }

        TempData["Success"] = $"Template '{template.Name}' updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var template = await db.PosterTemplates
            .FirstOrDefaultAsync(t => t.Id == id && !t.IsSystem
                && (t.TenantId == _tenant.TenantId || _tenant.IsAdmin), ct);

        if (template is null)
        {
            return NotFound();
        }

        // Detach any posters that reference this template (FK is Restrict).
        var posters = await db.Posters.Where(p => p.TemplateId == id).ToListAsync(ct);
        foreach (var p in posters)
        {
            p.TemplateId = null;
            p.TemplateName = null;
        }

        db.PosterTemplates.Remove(template);
        await db.SaveChangesAsync(ct);

TempData["Success"] = $"Template '{template.Name}' deleted.";
        return RedirectToAction(nameof(Index));
    }

    private static readonly string[] LogoExtensions = { ".png", ".jpg", ".jpeg", ".webp" };
    private const long MaxLogoBytes = 4 * 1024 * 1024;

    /// <summary>Serves the per-template uploaded logo image (or 404 when none).</summary>
    [HttpGet]
    public async Task<IActionResult> TemplateLogo(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var template = await db.PosterTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id
                && (_tenant.IsAdmin || t.TenantId == _tenant.TenantId || t.TenantId == 0), ct);

        if (template is null || template.LogoBytes is not { Length: > 0 })
        {
            return NotFound();
        }

        return File(template.LogoBytes, template.LogoMime ?? "image/png");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadLogo(int id, IFormFile? logo, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var template = await db.PosterTemplates
            .FirstOrDefaultAsync(t => t.Id == id && !t.IsSystem
                && (t.TenantId == _tenant.TenantId || _tenant.IsAdmin), ct);

        if (template is null)
        {
            return NotFound();
        }

        if (logo is null || logo.Length == 0)
        {
            TempData["Error"] = "Choose a logo file first.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        if (logo.Length > MaxLogoBytes)
        {
            TempData["Error"] = "Logo must be 4 MB or smaller.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        var extension = Path.GetExtension(logo.FileName).ToLowerInvariant();
        if (!LogoExtensions.Contains(extension))
        {
            TempData["Error"] = "Logo must be a PNG, JPG or WEBP image.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        using var ms = new MemoryStream();
        await logo.CopyToAsync(ms, ct);

        // Reject non-image content defensively (skipped for tiny files is not a concern here).
        ms.Position = 0;
        var probe = new byte[16];
        var read = ms.Read(probe, 0, probe.Length);
        var isImage = read >= 4
            && ((probe[0] == 0x89 && probe[1] == 0x50 && probe[2] == 0x4E && probe[3] == 0x47)   // PNG
                || (probe[0] == 0xFF && probe[1] == 0xD8)                                      // JPEG
                || (probe[0] == 0x52 && probe[1] == 0x49 && probe[2] == 0x46 && probe[3] == 0x46)); // WEBP (RIFF....WEBP)
        if (!isImage)
        {
            TempData["Error"] = "That file does not look like a PNG, JPG or WEBP image.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        template.LogoBytes = ms.ToArray();
        template.LogoMime = extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        };
        template.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Regenerate the thumbnail so the gallery reflects the new logo.
        template.ThumbnailBytes = null;
        template.ThumbnailPath = null;
        await DeleteCachedThumbnailAsync(id, ct);
        var path = await _thumbnails.EnsureThumbnailAsync(template, ct);
        if (!string.IsNullOrWhiteSpace(path))
        {
            var entity = await db.PosterTemplates.FindAsync([id], ct);
            if (entity is not null)
            {
                entity.ThumbnailPath = path;
                entity.ThumbnailBytes = template.ThumbnailBytes;
            }

            await db.SaveChangesAsync(ct);
        }

        TempData["Success"] = "Logo uploaded â€” it now appears on this template's posters and thumbnail.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLogo(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var template = await db.PosterTemplates
            .FirstOrDefaultAsync(t => t.Id == id && !t.IsSystem
                && (t.TenantId == _tenant.TenantId || _tenant.IsAdmin), ct);

        if (template is null)
        {
            return NotFound();
        }

        template.LogoBytes = null;
        template.LogoMime = null;
        template.UpdatedAt = DateTime.UtcNow;
        template.ThumbnailBytes = null;
        template.ThumbnailPath = null;
        await db.SaveChangesAsync(ct);

        await DeleteCachedThumbnailAsync(id, ct);
        var path = await _thumbnails.EnsureThumbnailAsync(template, ct);
        if (!string.IsNullOrWhiteSpace(path))
        {
            var entity = await db.PosterTemplates.FindAsync([id], ct);
            if (entity is not null)
            {
                entity.ThumbnailPath = path;
                entity.ThumbnailBytes = template.ThumbnailBytes;
                await db.SaveChangesAsync(ct);
            }
        }

        TempData["Success"] = "Logo removed.";
        return RedirectToAction(nameof(Edit), new { id });
    }

/// <summary>Deletes the on-disk cached thumbnail for a template so the next
    /// EnsureThumbnailAsync call regenerates it from the current settings.</summary>
    private async Task DeleteCachedThumbnailAsync(int id, CancellationToken ct)
    {
        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var file = Path.Combine(webRoot, "templates", $"template_{id}.png");
        try
        {
            if (System.IO.File.Exists(file))
            {
                System.IO.File.Delete(file);
            }
        }
        catch
        {
            // best effort; the DB snapshot is authoritative
        }

        await Task.CompletedTask;
    }

    /// <summary>Returns <paramref name="desired"/> untouched when free within the
    /// workspace, otherwise the first free "name (2)", "name (3)" variant - template
    /// names are unique per tenant, so imports never collide with existing rows.</summary>
    private static async Task<string> UniqueTemplateName(
        DailyPosterDbContext db, int tenantId, string desired, int? excludeId, CancellationToken ct)
    {
        var names = (await db.PosterTemplates.AsNoTracking()
                .Where(t => t.TenantId == tenantId && t.Id != excludeId)
                .Select(t => t.Name)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var name = desired.Trim();
        if (!names.Contains(name))
        {
            return name;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{name} ({DateTime.UtcNow.Ticks})";
    }

    private static List<ImportBox> ParseBoxes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<ImportBox>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<ImportBox>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<ImportBox>();
        }
        catch (JsonException)
        {
            return new List<ImportBox>();
        }
    }
}
