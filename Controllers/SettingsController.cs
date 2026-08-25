using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services;
using DailyPosterGenerator.Services.MultiTenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Controllers;

[Authorize]
public class SettingsController : Controller
{
    private static readonly string[] LogoExtensions = { ".png", ".jpg", ".jpeg", ".webp" };
    private const long MaxLogoBytes = 2 * 1024 * 1024;

    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly ISettingsService _settings;
    private readonly ITextGenerationService _text;
    private readonly TenantContext _tenant;
    private readonly IWebHostEnvironment _env;

    public SettingsController(
        IDbContextFactory<DailyPosterDbContext> dbFactory,
        ISettingsService settings,
        ITextGenerationService text,
        TenantContext tenant,
        IWebHostEnvironment env)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _text = text;
        _tenant = tenant;
        _env = env;
    }

    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var tenantLogo = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == _tenant.TenantId)
            .Select(t => t.LogoPath)
            .FirstOrDefaultAsync(ct);

        var vm = new SettingsViewModel
        {
            AiEnabled = bool.Parse(await _settings.GetAsync("ai.enabled", "true") ?? "true"),
            AiEndpoint = await _settings.GetAsync("ai.endpoint", "https://api.openai.com/v1"),
            AiApiKey = await _settings.GetAsync("ai.apiKey", ""),
            AiChatModel = await _settings.GetAsync("ai.chatModel", "gpt-4o-mini"),
            AiImageModel = await _settings.GetAsync("ai.imageModel", "dall-e-3"),
            AiGenerateImages = bool.Parse(await _settings.GetAsync("ai.generateImages", "false") ?? "false"),
            AiTimeoutSeconds = int.Parse(await _settings.GetAsync("ai.timeoutSeconds", "90") ?? "90"),
            SchedulerEnabled = bool.Parse(await _settings.GetAsync("scheduler.enabled", "true") ?? "true"),
            SchedulerTime = await _settings.GetAsync("scheduler.time", "06:00"),
            PosterTheme = await GetOrgAsync("theme", "auto", "auto"),
            OrganizationName = await GetOrgAsync("name", ""),
            OrganizationCity = await GetOrgAsync("city", ""),
            OrganizationTagline = await GetOrgAsync("tagline", ""),
            OrganizationFacebook = await GetOrgAsync("facebook", ""),
            OrganizationInstagram = await GetOrgAsync("instagram", ""),
            OrganizationPhones = await GetOrgAsync("phones", ""),
            OrganizationShowValues = bool.Parse(await GetOrgAsync("showValues", "true") ?? "true"),
            OrganizationValues = await GetOrgAsync("values", "Quality,Service,Trust,Excellence,Community"),
            AiActuallyConfigured = await _text.IsConfiguredAsync(),
            Platforms = await db.Platforms.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct)
        };

        ViewBag.TenantLogo = tenantLogo;
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadLogo(IFormFile? logo, CancellationToken ct = default)
    {
        if (logo is null || logo.Length == 0)
        {
            TempData["Error"] = "Choose a logo file first.";
            return RedirectToAction(nameof(Index));
        }

        if (logo.Length > MaxLogoBytes)
        {
            TempData["Error"] = "Logo must be 2 MB or smaller.";
            return RedirectToAction(nameof(Index));
        }

        var extension = Path.GetExtension(logo.FileName).ToLowerInvariant();
        if (!LogoExtensions.Contains(extension))
        {
            TempData["Error"] = "Logo must be a PNG, JPG or WEBP image.";
            return RedirectToAction(nameof(Index));
        }

        var wwwRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var logoDir = Path.Combine(wwwRoot, "logos");
        Directory.CreateDirectory(logoDir);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == _tenant.TenantId, ct);
        if (tenant is null)
        {
            TempData["Error"] = "Tenant not found.";
            return RedirectToAction(nameof(Index));
        }

        // Delete the previous file so the logos folder never accumulates orphans.
        if (!string.IsNullOrWhiteSpace(tenant.LogoPath))
        {
            DeleteLogoFile(wwwRoot, tenant.LogoPath);
        }

        var fileName = $"tenant_{tenant.Id}{extension}";
        var fullPath = Path.Combine(logoDir, fileName);
        await using (var stream = System.IO.File.Create(fullPath))
        {
            await logo.CopyToAsync(stream, ct);
        }

        tenant.LogoPath = "/logos/" + fileName;
        await db.SaveChangesAsync(ct);

        TempData["Success"] = "Logo uploaded — it will appear on newly generated posters.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLogo(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == _tenant.TenantId, ct);
        if (tenant is not null && !string.IsNullOrWhiteSpace(tenant.LogoPath))
        {
            var wwwRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            DeleteLogoFile(wwwRoot, tenant.LogoPath);
            tenant.LogoPath = null;
            await db.SaveChangesAsync(ct);
        }

        TempData["Success"] = "Logo removed.";
        return RedirectToAction(nameof(Index));
    }

    private static void DeleteLogoFile(string wwwRoot, string relativePath)
    {
        try
        {
            // Only ever delete inside wwwroot/logos.
            if (!relativePath.StartsWith("/logos/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var full = Path.Combine(wwwRoot, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(full))
            {
                System.IO.File.Delete(full);
            }
        }
        catch (IOException)
        {
            // Best effort; the DB row is already cleared.
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SettingsViewModel vm, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            vm.Platforms = await db.Platforms.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
            vm.AiActuallyConfigured = await _text.IsConfiguredAsync();
            return View("Index", vm);
        }

        await _settings.SetAsync("ai.enabled", vm.AiEnabled.ToString());
        await _settings.SetAsync("ai.endpoint", vm.AiEndpoint ?? string.Empty);
        await _settings.SetAsync("ai.apiKey", vm.AiApiKey ?? string.Empty);
        await _settings.SetAsync("ai.chatModel", vm.AiChatModel ?? "gpt-4o-mini");
        await _settings.SetAsync("ai.imageModel", vm.AiImageModel ?? "dall-e-3");
        await _settings.SetAsync("ai.generateImages", vm.AiGenerateImages.ToString());
        await _settings.SetAsync("ai.timeoutSeconds", vm.AiTimeoutSeconds.ToString());
        await _settings.SetAsync("scheduler.enabled", vm.SchedulerEnabled.ToString());
        await _settings.SetAsync("scheduler.time", vm.SchedulerTime ?? "06:00");
        await SetOrgAsync("theme", vm.PosterTheme ?? "auto");
        await SetOrgAsync("name", vm.OrganizationName ?? string.Empty);
        await SetOrgAsync("city", vm.OrganizationCity ?? string.Empty);
        await SetOrgAsync("tagline", vm.OrganizationTagline ?? string.Empty);
        await SetOrgAsync("facebook", vm.OrganizationFacebook ?? string.Empty);
        await SetOrgAsync("instagram", vm.OrganizationInstagram ?? string.Empty);
        await SetOrgAsync("phones", vm.OrganizationPhones ?? string.Empty);
        await SetOrgAsync("showValues", vm.OrganizationShowValues.ToString());
        await SetOrgAsync("values", vm.OrganizationValues ?? string.Empty);

        TempData["Success"] = "Settings saved.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<string?> GetOrgAsync(string key, string? defaultValue, string? legacyDefault = null)
    {
        var value = await _settings.GetAsync($"org.{key}", null);
        if (value is not null)
        {
            return value;
        }

        return await _settings.GetAsync($"school.{key}", defaultValue ?? legacyDefault);
    }

    private Task SetOrgAsync(string key, string value) => _settings.SetAsync($"org.{key}", value);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePlatform(Platform platform, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Platforms.FirstOrDefaultAsync(p => p.Name == platform.Name, ct);
        if (existing is null)
        {
            db.Platforms.Add(platform);
        }
        else
        {
            existing.Enabled = platform.Enabled;
            existing.WebhookUrl = platform.WebhookUrl;
            existing.AccountHandle = platform.AccountHandle;
        }

        await db.SaveChangesAsync(ct);
        TempData["Success"] = $"Platform '{platform.Name}' saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePlatform(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var platform = await db.Platforms.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (platform is not null)
        {
            db.Platforms.Remove(platform);
            await db.SaveChangesAsync(ct);
        }

        return RedirectToAction(nameof(Index));
    }
}
