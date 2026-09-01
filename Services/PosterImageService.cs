using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace DailyPosterGenerator.Services;

public interface IPosterImageService
{
    Task<string> GenerateAsync(Poster poster, string? themeOverride = null, string? accentOverride = null, PosterTemplate? template = null, bool logosOnly = false, CancellationToken ct = default);

    /// <summary>
    /// Renders a poster using the same pipeline but writes the result under
    /// wwwroot/posters/previews/ instead of the normal gallery, for live previews
    /// that must not pollute the poster archive.
    /// </summary>
    Task<string> RenderPreviewAsync(Poster poster, string? themeOverride = null, string? accentOverride = null, PosterTemplate? template = null, CancellationToken ct = default);
}

public class SkiaSharpPosterImageService : IPosterImageService
{
    private readonly IWebHostEnvironment _env;
    private readonly ISettingsService _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly ILogger<SkiaSharpPosterImageService> _logger;

    private const int Width = 1080;
    private const int Height = 1350;
    private const int SrvHeight = 1536;
    private const int FrameInset = 28;

    public SkiaSharpPosterImageService(
        IWebHostEnvironment env,
        ISettingsService settings,
        IHttpClientFactory httpFactory,
        IDbContextFactory<DailyPosterDbContext> dbFactory,
        ILogger<SkiaSharpPosterImageService> logger)
    {
        _env = env;
        _settings = settings;
        _httpFactory = httpFactory;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    private static SKTypeface SafeTypeface(string family, SKFontStyle style)
    {
        var tf = SKTypeface.FromFamilyName(family, style);
        if (tf is null && !string.Equals(family, "DejaVu Sans", StringComparison.OrdinalIgnoreCase))
        {
            tf = SKTypeface.FromFamilyName("DejaVu Sans", style);
        }
        tf ??= SKTypeface.Default;
        if (tf is null)
        {
            throw new InvalidOperationException($"No usable font typeface for '{family}'.");
        }
        return tf;
    }

    public async Task<string> GenerateAsync(Poster poster, string? themeOverride = null, string? accentOverride = null, PosterTemplate? template = null, bool logosOnly = false, CancellationToken ct = default)
    {
        return await RenderAsync(poster, themeOverride, accentOverride, template, preview: false, logosOnly: logosOnly, ct);
    }

    public async Task<string> RenderPreviewAsync(Poster poster, string? themeOverride = null, string? accentOverride = null, PosterTemplate? template = null, CancellationToken ct = default)
    {
        return await RenderAsync(poster, themeOverride, accentOverride, template, preview: true, logosOnly: false, ct);
    }

    private async Task<string> RenderAsync(Poster poster, string? themeOverride, string? accentOverride, PosterTemplate? template, bool preview, bool logosOnly, CancellationToken ct)
    {
        var brand = await BuildBrandAsync(ct);

        var templateMode = template is not null
            && string.Equals(template.Theme, "template", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(template.BackgroundImagePath);

        SKImage? aiBackground = null;
        if (!templateMode &&
            bool.Parse(await _settings.GetAsync("ai.generateImages", "false") ?? "false") &&
            !string.IsNullOrWhiteSpace(await _settings.GetAsync("ai.apiKey")))
        {
            try
            {
                aiBackground = await FetchAiBackgroundAsync(poster, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI image generation failed; falling back to template.");
            }
        }

        var theme = await BuildThemeAsync(poster, aiBackground is not null, themeOverride, accentOverride, ct);

        var info = new SKImageInfo(Width, theme.IsSrv ? SrvHeight : Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Failed to create surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        if (templateMode)
        {
            if (logosOnly)
            {
                DrawTemplateBackgroundOnly(canvas, template!, brand);
            }
            else
            {
                DrawTemplateLayout(canvas, poster, template!, brand);
            }
        }
        else
        {
            SKImage? hero = null;
            try
            {
                hero = await FetchHeroImageAsync(poster, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Hero image fetch failed.");
            }

            if (theme.IsSrv)
            {
                DrawSrvBackground(canvas, theme);
                if (logosOnly)
                {
                    DrawSrvLogosOnly(canvas, theme);
                }
                else
                {
                    DrawSrvContent(canvas, poster, theme, brand, hero);
                }
            }
            else if (aiBackground is not null)
            {
                DrawCover(canvas, aiBackground);
                DrawOverlay(canvas);
            }
            else
            {
                DrawBackground(canvas, poster, theme);
            }

            if (!theme.IsSrv && !logosOnly)
            {
                DrawContent(canvas, poster, theme, brand, hero, template?.Id ?? poster.TemplateId ?? 0);
            }

            hero?.Dispose();
        }

        // Per-template uploaded logo wins; otherwise fall back to the tenant's logo.
        // The template mode layouts and srv theme do not overlay a tenant logo by default,
        // but an explicitly uploaded template logo is always respected.
        if (template?.LogoBytes is { Length: > 0 })
        {
            using var templateLogo = await LoadTemplateLogoAsync(template.LogoBytes, ct);
            if (templateLogo is not null)
            {
                DrawTenantLogo(canvas, templateLogo, template.LogoPosition);
            }
        }
        else if (!templateMode && !theme.IsSrv)
        {
            using var logo = await LoadTenantLogoAsync(poster.TenantId, ct);
            if (logo is not null)
            {
                DrawTenantLogo(canvas, logo, template?.LogoPosition);
            }
        }

        canvas.Flush();

        var imagePath = SaveSurface(surface, poster, preview);
        aiBackground?.Dispose();
        return imagePath;
    }

    private async Task<Brand> BuildBrandAsync(CancellationToken ct)
    {
        return new Brand
        {
            Name = (await GetOrgAsync("name", "", "Sri Ramakrishna Vidyalayam"))?.Trim() ?? "",
            City = (await GetOrgAsync("city", "", "Khammam"))?.Trim() ?? "",
            Tagline = (await GetOrgAsync("tagline", "", ""))?.Trim() ?? "",
            Facebook = (await GetOrgAsync("facebook", "", "@srkvkmm"))?.Trim() ?? "",
            Instagram = (await GetOrgAsync("instagram", "", "@sriramakrishnavidyalayam_"))?.Trim() ?? "",
            Phones = (await GetOrgAsync("phones", "", "8897384856 8498928295 9030028295"))?.Trim() ?? "",
            ShowValues = bool.Parse(await GetOrgAsync("showValues", "true", "true") ?? "true"),
            Values = ((await GetOrgAsync("values", "Quality,Service,Trust,Excellence,Community", "Compassion,Respect,Discipline,Inclusion,Empowerment,Service"))
                ?? "Quality,Service,Trust,Excellence,Community")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(6)
                .ToArray()
        };
    }

    private async Task<string?> GetOrgAsync(string key, string defaultValue, string legacyDefault)
    {
        var value = await _settings.GetAsync($"org.{key}", null);
        if (value is not null)
        {
            return value;
        }

        return await _settings.GetAsync($"school.{key}", legacyDefault);
    }

    private async Task<SKImage?> FetchHeroImageAsync(Poster poster, CancellationToken ct)
    {
        var url = poster.Events.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Url))?.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        const string marker = "/wiki/";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var slug = url[(idx + marker.Length)..].Split('#', '?')[0].TrimEnd('/');
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var http = _httpFactory.CreateClient("wiki");
        using var resp = await http.GetAsync(
            $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(slug)}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("thumbnail", out var thumb) ||
            !thumb.TryGetProperty("source", out var src))
        {
            return null;
        }

        var imgUrl = src.GetString();
        if (string.IsNullOrWhiteSpace(imgUrl))
        {
            return null;
        }

        using var imgResp = await http.GetAsync(imgUrl, ct);
        if (!imgResp.IsSuccessStatusCode)
        {
            return null;
        }

        var bytes = await imgResp.Content.ReadAsByteArrayAsync(ct);
        return SKImage.FromEncodedData(bytes);
    }

    private async Task<PosterTheme> BuildThemeAsync(Poster poster, bool aiBackground, string? themeOverride, string? accentOverride, CancellationToken ct)
    {
        var setting = (themeOverride ?? await _settings.GetAsync("poster.theme", "colorful") ?? "colorful").Trim().ToLowerInvariant();

        string mode;
        if (aiBackground)
        {
            mode = "dark";
        }
        else if (setting is "light" or "dark" or "colorful" or "srv"
            || PosterTheme.SignatureModes.Contains(setting, StringComparer.OrdinalIgnoreCase))
        {
            mode = setting;
        }
        else
        {
            // auto: pick a mode by a stable hash of date + category
            var hash = Math.Abs((poster.EventDate.Year * 10000 + poster.EventDate.Month * 100 + poster.EventDate.Day) * 31
                + (string.IsNullOrWhiteSpace(poster.Category) ? 0 : poster.Category.GetHashCode(StringComparison.OrdinalIgnoreCase)));
            mode = (new[] { "colorful", "light", "dark" })[hash % 3];
        }

        var palette = PosterPalettes.Pick(poster, mode);
        var theme = PosterTheme.From(palette, mode);
        if (!string.IsNullOrWhiteSpace(accentOverride))
        {
            theme = theme.WithAccent(accentOverride);
        }

        return theme;
    }

    // ---------------------------------------------------------------- background

    private void DrawBackground(SKCanvas canvas, Poster poster, PosterTheme theme)
    {
        using var bg = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(Width, Height),
                new[] { theme.Gradient[0], theme.Gradient[1] },
                new[] { 0f, 1f },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(0, 0, Width, Height), bg);

        // Soft radial glow near the top-left of the content area.
        using var glow = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(Width * 0.32f, Height * 0.34f),
                620f,
                new[] { theme.Glow, new SKColor(theme.Glow.Red, theme.Glow.Green, theme.Glow.Blue, 0) },
                new[] { 0f, 1f },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(0, 0, Width, Height), glow);

        // Giant day-of-month numeral as a background watermark.
        using (var numeralPaint = new SKPaint { Color = theme.Watermark, IsAntialias = true })
        using (var numeralFont = new SKFont(SafeTypeface("Georgia", SKFontStyle.Bold), 560))
        {
            var numeral = poster.EventDate.Day.ToString();
            var numWidth = numeralFont.MeasureText(numeral);
            canvas.DrawText(numeral, Width - numWidth - 60, 610, SKTextAlign.Left, numeralFont, numeralPaint);
        }

        // Decorative stroke circles.
        DrawDecorativeCircles(canvas, theme);

        // Film-grain noise for a premium print feel.
        DrawNoise(canvas, poster.EventDate, theme.Noise);
    }

    private void DrawDecorativeCircles(SKCanvas canvas, PosterTheme theme)
    {
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            Color = theme.Frame
        };
        canvas.DrawCircle(930, 1180, 240, stroke);

        using var fill = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = theme.IsColorful
                ? new SKColor(theme.Accent.Red, theme.Accent.Green, theme.Accent.Blue, 60)
                : new SKColor(theme.Accent.Red, theme.Accent.Green, theme.Accent.Blue, 26)
        };
        canvas.DrawCircle(930, 1180, 148, fill);

        using var small = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = new SKColor(theme.Secondary.Red, theme.Secondary.Green, theme.Secondary.Blue, 90)
        };
        canvas.DrawCircle(170, 1220, 9, small);
        canvas.DrawCircle(196, 1244, 4, small);
    }

    private void DrawNoise(SKCanvas canvas, DateTime date, SKColor color)
    {
        var seed = date.Year * 1000 + date.DayOfYear;
        var rng = new Random(seed);
        using var dot = new SKPaint { Style = SKPaintStyle.Fill, Color = color };
        for (var i = 0; i < 900; i++)
        {
            var x = (float)(rng.NextDouble() * Width);
            var y = (float)(rng.NextDouble() * Height);
            var r = 0.6f + (float)(rng.NextDouble() * 1.1f);
            canvas.DrawCircle(x, y, r, dot);
        }
    }

    private void DrawOverlay(SKCanvas canvas)
    {
        using var overlay = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, Height),
                new[] { new SKColor(0, 0, 0, 150), new SKColor(0, 0, 0, 10), new SKColor(0, 0, 0, 175) },
                new[] { 0f, 0.5f, 1f },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(0, 0, Width, Height), overlay);
    }

    private static void DrawCover(SKCanvas canvas, SKImage image)
    {
        var scale = Math.Max(Width / (float)image.Width, Height / (float)image.Height);
        var drawWidth = image.Width * scale;
        var drawHeight = image.Height * scale;
        var x = (Width - drawWidth) / 2f;
        var y = (Height - drawHeight) / 2f;
        canvas.DrawImage(image, new SKRect(x, y, x + drawWidth, y + drawHeight), SKSamplingOptions.Default, null);
    }

    // ------------------------------------------------------- imported template layout

    private static readonly JsonSerializerOptions RegionJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class TemplateTextRegion
    {
        public string Key { get; set; } = string.Empty;
        public float X { get; set; } = 0.08f;
        public float Y { get; set; } = 0.08f;
        public float W { get; set; } = 0.84f;
        public float H { get; set; } = 0.12f;
        public float FontSize { get; set; } = 0.04f;
        public string Align { get; set; } = "center";
        public bool Bold { get; set; } = true;
    }

    private void DrawTemplateLayout(SKCanvas canvas, Poster poster, PosterTemplate template, Brand brand)
    {
        var wwwRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var rel = template.BackgroundImagePath!.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.Combine(wwwRoot, rel);

        if (File.Exists(full))
        {
            using var bg = SKImage.FromEncodedData(full);
            DrawCover(canvas, bg);
        }
        else
        {
            DrawBackground(canvas, poster, PosterTheme.From(new[] { new SKColor(24, 26, 36), new SKColor(64, 24, 70) }, "dark"));
        }

        var dim = Math.Clamp(template.BackgroundDim, 0, 90);
        if (dim > 0)
        {
            using var dimPaint = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(dim * 255 / 100)) };
            canvas.DrawRect(new SKRect(0, 0, Width, Height), dimPaint);
        }

        var textColor = HexColor.TryParse(template.TextColor, out var tc) ? tc : SKColors.White;
        var accent = HexColor.TryParse(template.AccentColor, out var ac) ? ac : new SKColor(255, 102, 0);

        using var sans = SafeTypeface("Segoe UI", SKFontStyle.Normal);
        using var sansBold = SafeTypeface("Segoe UI", SKFontStyle.Bold);

        // Accent underline bar under the header.
        using (var bar = new SKPaint { Color = accent, IsAntialias = true })
        {
            canvas.DrawRoundRect(SKRect.Create(Width * 0.32f, Height * 0.115f, Width * 0.36f, 6), 3, 3, bar);
        }

        var content = BuildTemplateContent(poster, brand);
        foreach (var region in ParseRegions(template.TextRegionsJson))
        {
            if (!content.TryGetValue(region.Key, out var text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var rect = SKRect.Create(region.X * Width, region.Y * Height, region.W * Width, region.H * Height);
            using var font = new SKFont(region.Bold ? sansBold : sans, region.FontSize * Height);
            DrawRegionText(canvas, rect, text, font, region.Align, textColor);
        }
    }

    /// <summary>
    /// Renders only the template background (image + dim overlay + accent bar)
    /// without any text regions — used for the "keep logos" regeneration mode.
    /// </summary>
    private void DrawTemplateBackgroundOnly(SKCanvas canvas, PosterTemplate template, Brand brand)
    {
        var wwwRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var rel = template.BackgroundImagePath!.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.Combine(wwwRoot, rel);

        if (File.Exists(full))
        {
            using var bg = SKImage.FromEncodedData(full);
            DrawCover(canvas, bg);
        }
        else
        {
            DrawBackground(canvas, new Poster(), PosterTheme.From(new[] { new SKColor(24, 26, 36), new SKColor(64, 24, 70) }, "dark"));
        }

        var dim = Math.Clamp(template.BackgroundDim, 0, 90);
        if (dim > 0)
        {
            using var dimPaint = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(dim * 255 / 100)) };
            canvas.DrawRect(new SKRect(0, 0, Width, Height), dimPaint);
        }
    }

    /// <summary>
    /// Draws the SRV school header emblems and footer crest without any text —
    /// used for the "keep logos" regeneration mode on school-layout posters.
    /// </summary>
    private void DrawSrvLogosOnly(SKCanvas canvas, PosterTheme theme)
    {
        var navy = theme.Accent;
        var gold = theme.Secondary;

        // Header emblems
        using (var lamp = LoadSrvBrandImage("lamp.png"))
        {
            if (lamp is not null)
            {
                var dest = SKRect.Create(Width * 0.38f, 18f, 70f, 80f);
                canvas.DrawImage(lamp, dest, SKSamplingOptions.Default, new SKPaint { IsAntialias = true });
            }
        }

        using (var banner = LoadSrvBrandImage("banner.png"))
        {
            if (banner is not null)
            {
                var dest = SKRect.Create(Width * 0.47f, 8f, Width * 0.06f, 96f);
                canvas.DrawImage(banner, dest, SKSamplingOptions.Default, new SKPaint { IsAntialias = true });
            }
        }

        using (var vp = LoadSrvBrandImage("vidyapeetham.png"))
        {
            if (vp is not null)
            {
                var dest = SKRect.Create(Width * 0.54f, 18f, 70f, 80f);
                canvas.DrawImage(vp, dest, SKSamplingOptions.Default, new SKPaint { IsAntialias = true });
            }
        }

        // Footer crest
        var logoSize = 90f;
        using (var logo = LoadSrvCrestImage())
        {
            if (logo is not null)
            {
                canvas.DrawImage(logo, SKRect.Create(70f, SrvHeight - logoSize - 30f, logoSize, logoSize), SKSamplingOptions.Default, new SKPaint { IsAntialias = true });
            }
            else
            {
                DrawSrvYearsBadge(canvas, 136, 1305, navy, gold);
            }
        }
    }

    private static Dictionary<string, string> BuildTemplateContent(Poster poster, Brand brand)
    {
        var d = new Dictionary<string, string>
        {
            ["header"] = string.IsNullOrWhiteSpace(brand.Name) ? "DAILY POSTER" : brand.Name.ToUpperInvariant(),
            ["date"] = poster.EventDate.ToString("dddd, dd MMM yyyy").ToUpperInvariant(),
            ["title"] = poster.EventTitle,
            ["caption"] = poster.Caption ?? string.Empty,
            ["values"] = brand.ShowValues ? string.Join("    ", brand.Values.Select(v => v.ToUpperInvariant())) : string.Empty,
            ["footer"] = string.Join("    ",
                new[] { brand.Tagline, brand.City, brand.Facebook, brand.Instagram, brand.Phones }
                    .Where(s => !string.IsNullOrWhiteSpace(s)))
        };
        return d;
    }

    private static List<TemplateTextRegion> ParseRegions(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<TemplateTextRegion>>(json, RegionJsonOptions);
                if (parsed is { Count: > 0 })
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // fall through to defaults
            }
        }

        return new List<TemplateTextRegion>
        {
            new() { Key = "header", X = 0.05f, Y = 0.05f, W = 0.9f, H = 0.07f, FontSize = 0.028f, Align = "center", Bold = true },
            new() { Key = "date", X = 0.05f, Y = 0.14f, W = 0.9f, H = 0.045f, FontSize = 0.019f, Align = "center", Bold = false },
            new() { Key = "title", X = 0.08f, Y = 0.22f, W = 0.84f, H = 0.2f, FontSize = 0.048f, Align = "center", Bold = true },
            new() { Key = "caption", X = 0.12f, Y = 0.46f, W = 0.76f, H = 0.16f, FontSize = 0.026f, Align = "center", Bold = false },
            new() { Key = "values", X = 0.06f, Y = 0.88f, W = 0.88f, H = 0.06f, FontSize = 0.021f, Align = "center", Bold = true },
            new() { Key = "footer", X = 0.06f, Y = 0.945f, W = 0.88f, H = 0.04f, FontSize = 0.016f, Align = "center", Bold = false }
        };
    }

    private static void DrawRegionText(SKCanvas canvas, SKRect rect, string text, SKFont font, string align, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        var lines = WrapText(text, rect.Width - 24f, font);
        if (lines.Count == 0)
        {
            return;
        }

        var lineHeight = font.Size * 1.22f;
        var totalHeight = lineHeight * lines.Count;
        var startY = rect.MidY - totalHeight / 2f + lineHeight * 0.82f;

        var textAlign = align switch
        {
            "left" => SKTextAlign.Left,
            "right" => SKTextAlign.Right,
            _ => SKTextAlign.Center
        };

        for (var i = 0; i < lines.Count; i++)
        {
            var x = align switch
            {
                "left" => rect.Left + 12,
                "right" => rect.Right - 12,
                _ => rect.MidX
            };
            canvas.DrawText(lines[i], x, startY + i * lineHeight, textAlign, font, paint);
        }
    }

    private static List<string> WrapText(string text, float maxWidth, SKFont font)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = new StringBuilder();
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : $"{current} {word}";
                if (current.Length == 0 || font.MeasureText(candidate) <= maxWidth)
                {
                    current = new StringBuilder(candidate);
                }
                else
                {
                    result.Add(current.ToString());
                    current = new StringBuilder(word);
                }
            }

            if (current.Length > 0)
            {
                result.Add(current.ToString());
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- content

    private void DrawContent(SKCanvas canvas, Poster poster, PosterTheme theme, Brand brand, SKImage? hero = null, int templateId = 0)
    {
        const int margin = 92;
        const int usable = Width - (margin * 2);

        // The footer block (rule, values strip, school name, tagline) starts here and
        // must never be overwritten by flowing content, so body copy is clipped above it.
        const float footerTop = Height - 250;
        const float contentBottom = footerTop - 28;

        using var serif = SafeTypeface("Georgia", SKFontStyle.Bold);
        using var sans = SafeTypeface("Segoe UI", SKFontStyle.Normal);
        using var sansBold = SafeTypeface("Segoe UI", SKFontStyle.Bold);
        using var mono = SafeTypeface("Consolas", SKFontStyle.Normal);

        // ---- 1. Header row: unique per-template emblem badge ----------------
        DrawTemplateEmblem(canvas, theme, templateId, margin + 22, 84, 22f);
        using (var brandPaint = new SKPaint { Color = theme.Text, IsAntialias = true })
        using (var brandFont = new SKFont(sansBold, 25))
        {
            canvas.DrawText("DAILY POSTER", margin + 62, 91, SKTextAlign.Left, brandFont, brandPaint);
        }
        using (var subPaint = new SKPaint { Color = theme.Faint, IsAntialias = true })
        using (var subFont = new SKFont(sans, 17))
        {
            canvas.DrawText("AUTOMATED STORY TELLING", margin + 62, 114, SKTextAlign.Left, subFont, subPaint);
        }

        // Date chip (top right)
        using (var chipPaint = new SKPaint { Color = theme.Accent, IsAntialias = true })
        using (var chipTextPaint = new SKPaint { Color = theme.ChipText, IsAntialias = true })
        using (var chipFont = new SKFont(mono, 22))
        {
            var chipText = poster.EventDate.ToString("dd MMM").ToUpperInvariant();
            var chipW = chipFont.MeasureText(chipText) + 48;
            var chipRect = SKRect.Create(Width - margin - chipW, 62, chipW, 44);
            canvas.DrawRoundRect(chipRect, 22, 22, chipPaint);
            canvas.DrawText(chipText, chipRect.MidX, 90, SKTextAlign.Center, chipFont, chipTextPaint);
        }

        var category = string.IsNullOrWhiteSpace(poster.Category) ? "ON THIS DAY" : poster.Category.ToUpperInvariant();
        float y;

        if (hero is not null)
        {
            // ---- 2. Hero image panel --------------------------------------
            const float heroTop = 150f;
            const float heroHeight = 460f;
            var heroRect = SKRect.Create(margin, heroTop, usable, heroHeight);

            var heroClipBuilder = new SKPathBuilder();
            heroClipBuilder.AddRoundRect(heroRect, 28f, 28f, SKPathDirection.Clockwise);
            using var clip = heroClipBuilder.Detach();
            canvas.Save();
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);
            DrawCover(canvas, hero);

            // bottom blend into background
            using var heroShade = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, heroRect.Bottom - 180), new SKPoint(0, heroRect.Bottom),
                    new[] { new SKColor(0, 0, 0, 0), theme.IsLight ? new SKColor(0, 0, 0, 70) : new SKColor(0, 0, 0, 130) },
                    new[] { 0f, 1f },
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawRect(heroRect, heroShade);
            canvas.Restore();

            // category badge on the hero
            using (var badgePaint = new SKPaint { Color = theme.IsColorful ? theme.Secondary : theme.Accent, IsAntialias = true })
            using (var badgeTextPaint = new SKPaint { Color = theme.IsColorful ? new SKColor(20, 20, 30) : theme.ChipText, IsAntialias = true })
            using (var badgeFont = new SKFont(sansBold, 24))
            {
                var badgeW = badgeFont.MeasureText(category) + 40;
                canvas.DrawRoundRect(SKRect.Create(margin + 26, heroTop + 26, badgeW, 48), 24, 24, badgePaint);
                canvas.DrawText(category, margin + 26 + 20, heroTop + 60, SKTextAlign.Left, badgeFont, badgeTextPaint);
            }

            // date strip on the hero (bottom-left)
            using (var stripPaint = new SKPaint { Color = new SKColor(255, 255, 255, 235), IsAntialias = true })
            using (var stripFont = new SKFont(mono, 26))
            {
                var stripText = poster.EventDate.ToString("MMMM d").ToUpperInvariant();
                canvas.DrawText(stripText, margin + 26, heroRect.Bottom - 34, SKTextAlign.Left, stripFont, stripPaint);
            }

            y = heroRect.Bottom + 48;
        }
        else
        {
            // ---- 2. Category eyebrow --------------------------------------
            y = 210f;
            using (var rulePaint = new SKPaint { Color = theme.Accent, IsAntialias = true })
            {
                canvas.DrawRoundRect(SKRect.Create(margin, y - 7, 46, 4), 2, 2, rulePaint);
            }
            using (var eyebrowPaint = new SKPaint { Color = theme.Accent, IsAntialias = true })
            using (var eyebrowFont = new SKFont(sansBold, 26))
            {
                canvas.DrawText(category, margin + 62, y, SKTextAlign.Left, eyebrowFont, eyebrowPaint);
            }

            y += 34;
        }

        // ---- 3. Headline ---------------------------------------------------
        using (var titlePaint = new SKPaint { Color = theme.Text, IsAntialias = true })
        using (var shadowPaint = new SKPaint { Color = theme.Shadow, IsAntialias = true })
        using (var titleFont = new SKFont(serif, hero is not null ? 80 : 86))
        {
            var maxLines = Math.Clamp(
                (int)((contentBottom - y) / (hero is not null ? 90f : 96f)),
                1, hero is not null ? 4 : 5);
            var lines = WrapText(titleFont, poster.EventTitle, usable);
            foreach (var line in lines.Take(maxLines))
            {
                canvas.DrawText(line, margin + 3, y + 4, SKTextAlign.Left, titleFont, shadowPaint);
                canvas.DrawText(line, margin, y, SKTextAlign.Left, titleFont, titlePaint);
                y += hero is not null ? 90 : 96;
            }
        }

        // colorful title underline accent bar
        if (theme.IsColorful)
        {
            using var barPaint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(margin, 0), new SKPoint(margin + 260, 0),
                    new[] { theme.Accent, theme.Secondary },
                    new[] { 0f, 1f },
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawRoundRect(SKRect.Create(margin, y - 4, 260, 12), 6, 6, barPaint);
            y += 22;
        }
        else
        {
            y += 10;
        }

        // ---- 4. Year chip --------------------------------------------------
        var year = poster.Events.OrderBy(e => e.Kind == "selected" ? 0 : 1).FirstOrDefault()?.Year;
        if (year.HasValue && y + 58 <= footerTop)
        {
            using var chipPaint = new SKPaint { Color = theme.Accent, IsAntialias = true };
            using var chipTextPaint = new SKPaint { Color = theme.ChipText, IsAntialias = true };
            using var chipFont = new SKFont(mono, 26);
            var chipText = $"EST. {year}";
            var chipW = chipFont.MeasureText(chipText) + 44;
            canvas.DrawRoundRect(SKRect.Create(margin, y - 30, chipW, 58), 29, 29, chipPaint);
            canvas.DrawText(chipText, margin + 22, y + 4, SKTextAlign.Left, chipFont, chipTextPaint);
            y += 84;
        }

        // ---- 5. Description ------------------------------------------------
        if (!string.IsNullOrWhiteSpace(poster.Description))
        {
            using var descPaint = new SKPaint { Color = theme.Muted, IsAntialias = true };
            using var descFont = new SKFont(sans, 36);
            var maxDesc = Math.Min(hero is not null ? 3 : 6,
                Math.Max(0, (int)((contentBottom - y) / 51f)));
            foreach (var line in WrapText(descFont, poster.Description, usable).Take(maxDesc))
            {
                canvas.DrawText(line, margin, y, SKTextAlign.Left, descFont, descPaint);
                y += 51;
            }
        }

        // ---- 6. Footer -----------------------------------------------------
        using (var rulePaint = new SKPaint { Color = theme.Frame, IsAntialias = true })
        {
            canvas.DrawRect(SKRect.Create(margin, footerTop, usable, 2), rulePaint);
        }

        if (!string.IsNullOrWhiteSpace(poster.Hashtags))
        {
            using var tagPaint = new SKPaint { Color = theme.Tag, IsAntialias = true };
            using var tagFont = new SKFont(sansBold, 32);
            var tags = poster.Hashtags.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(7);
            foreach (var line in WrapText(tagFont, string.Join(" ", tags), usable).Take(2))
            {
                canvas.DrawText(line, margin, footerTop + 26, SKTextAlign.Left, tagFont, tagPaint);
            }
        }

        var hasBrand = !string.IsNullOrWhiteSpace(brand.Name);

        if (hasBrand)
        {
            // ---- school values strip ----------------------------------------
            if (brand.ShowValues && brand.Values.Length > 0)
            {
                var valueWords = brand.Values.Select(v => v.ToUpperInvariant()).ToArray();
                const string sep = "  ◆  ";
                using var valueFont = new SKFont(sansBold, theme.IsColorful ? 24 : 25);
                var sepW = valueFont.MeasureText(sep);
                var total = valueWords.Sum(v => valueFont.MeasureText(v)) + sepW * (valueWords.Length - 1);
                var x = (Width - total) / 2f;

                var wordColors = theme.IsColorful
                    ? new[] { theme.Accent, theme.Secondary, SKColors.White }
                    : new[] { theme.Accent, theme.Accent, theme.Accent };

                for (var i = 0; i < valueWords.Length; i++)
                {
                    using var paint = new SKPaint { Color = wordColors[i % wordColors.Length], IsAntialias = true };
                    canvas.DrawText(valueWords[i], x, Height - 150, SKTextAlign.Left, valueFont, paint);
                    x += valueFont.MeasureText(valueWords[i]);
                    if (i < valueWords.Length - 1)
                    {
                        using var sepPaint = new SKPaint { Color = theme.Faint, IsAntialias = true };
                        canvas.DrawText(sep, x, Height - 150, SKTextAlign.Left, valueFont, sepPaint);
                        x += sepW;
                    }
                }
            }

            // ---- school name / city / tagline -------------------------------
            using (var schoolPaint = new SKPaint { Color = theme.Text, IsAntialias = true })
            using (var schoolFont = new SKFont(sansBold, 32))
            {
                var schoolText = brand.Name.ToUpperInvariant();
                var city = string.IsNullOrWhiteSpace(brand.City) ? string.Empty : ", " + brand.City.ToUpperInvariant();
                canvas.DrawText(schoolText + city, margin, Height - 94, SKTextAlign.Left, schoolFont, schoolPaint);
            }

            if (!string.IsNullOrWhiteSpace(brand.Tagline))
            {
                using var taglinePaint = new SKPaint { Color = theme.Muted, IsAntialias = true };
                using var taglineFont = new SKFont(sans, 23);
                canvas.DrawText(brand.Tagline, margin, Height - 62, SKTextAlign.Left, taglineFont, taglinePaint);
            }
        }
        else
        {
            using (var brandPaint = new SKPaint { Color = theme.Text, IsAntialias = true })
            using (var brandFont = new SKFont(sansBold, 27))
            {
                canvas.DrawText("DAILY POSTER", margin, Height - 84, SKTextAlign.Left, brandFont, brandPaint);
            }

            using (var diamondPaint = new SKPaint { Color = theme.Accent, IsAntialias = true })
            {
                canvas.Save();
                canvas.Translate(margin + 118, Height - 71);
                canvas.RotateDegrees(45);
                canvas.DrawRect(SKRect.Create(-5, -5, 10, 10), diamondPaint);
                canvas.Restore();
            }
        }

        using (var providerPaint = new SKPaint { Color = theme.Faint, IsAntialias = true })
        using (var providerFont = new SKFont(sans, 22))
        {
            var provider = poster.AiProvider ?? "auto-generated";
            canvas.DrawText(provider, Width - margin, footerTop + 26, SKTextAlign.Right, providerFont, providerPaint);
        }

        // ---- 7. Inset frame ------------------------------------------------
        using (var framePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            Color = theme.Frame
        })
        {
            canvas.DrawRoundRect(SKRect.Create(FrameInset, FrameInset, Width - FrameInset * 2, Height - FrameInset * 2), 26, 26, framePaint);
        }
    }

    // ---------------------------------------------------------------- SRV school style

    private SKImage? LoadSrvBrandImage(string fileName)
    {
        try
        {
            var wwwRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var full = Path.Combine(wwwRoot, "branding", fileName);
            return File.Exists(full) ? SKImage.FromEncodedData(full) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load branding image {File}", fileName);
            return null;
        }
    }

    /// <summary>Loads the school crest and knocks out the solid white background
    /// (flood-fill from the edges) so it sits cleanly on the navy footer band.</summary>
    private SKImage? LoadSrvCrestImage()
    {
        using var src = LoadSrvBrandImage("school-crest.png");
        if (src is null)
        {
            return null;
        }

        try
        {
            using var bmp = new SKBitmap(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var bmpCanvas = new SKCanvas(bmp))
            {
                bmpCanvas.DrawColor(SKColors.White);
                bmpCanvas.DrawImage(src, 0f, 0f, SKSamplingOptions.Default);
            }

            var w = bmp.Width;
            var h = bmp.Height;
            var visited = new bool[w * h];
            var queue = new Queue<(int X, int Y)>();

            bool IsWhite(SKColor c) => c.Red >= 242 && c.Green >= 242 && c.Blue >= 242;

            void Push(int x, int y)
            {
                if (x < 0 || x >= w || y < 0 || y >= h)
                {
                    return;
                }

                var i = y * w + x;
                if (visited[i] || !IsWhite(bmp.GetPixel(x, y)))
                {
                    return;
                }

                visited[i] = true;
                queue.Enqueue((x, y));
            }

            for (var x = 0; x < w; x++)
            {
                Push(x, 0);
                Push(x, h - 1);
            }

            for (var y = 0; y < h; y++)
            {
                Push(0, y);
                Push(w - 1, y);
            }

            while (queue.Count > 0)
            {
                var (x, y) = queue.Dequeue();
                bmp.SetPixel(x, y, new SKColor(0, 0, 0, 0));
                Push(x - 1, y);
                Push(x + 1, y);
                Push(x, y - 1);
                Push(x, y + 1);
            }

            return SKImage.FromBitmap(bmp);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to knock out school crest background.");
            return src;
        }
    }

    private void DrawTemplateEmblem(SKCanvas canvas, PosterTheme theme, int templateId, float cx, float cy, float r)
    {
        var hue = (templateId * 53.0f) % 360f;
        theme.Accent.ToHsl(out var h, out var s, out var l);
        var primary = SKColor.FromHsl((h + hue) % 360f, Math.Min(1f, s + 0.06f), Math.Clamp(l, 0.26f, 0.58f));
        var rim = SKColor.FromHsl((h + hue + 48f) % 360f, Math.Min(1f, s + 0.14f), Math.Clamp(l + 0.28f, 0.55f, 0.88f));

        float lum = 0.299f * primary.Red + 0.587f * primary.Green + 0.114f * primary.Blue;
        var glyphColor = lum < 130f ? new SKColor(255, 255, 255, 235) : new SKColor(26, 26, 40, 225);

        using var badge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = primary };
        using var rimPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = r * 0.16f, Color = rim };
        canvas.DrawCircle(cx, cy, r, badge);
        canvas.DrawCircle(cx, cy, r, rimPaint);

        var glyph = Math.Abs(templateId) % 12;
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = glyphColor };
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = r * 0.30f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = glyphColor
        };
        var scale = r / 10f;

        switch (glyph)
        {
            case 0: // star
                DrawEmblemPath(canvas, "M0,-10 L2.36,-3.09 L9.51,-3.09 L3.82,1.18 L5.88,8.09 L0,4 L-5.88,8.09 L-3.82,1.18 L-9.51,-3.09 L-2.36,-3.09 Z", cx, cy, scale, fill);
                break;
            case 1: // moon (crescent)
                using (var moon = SKPath.ParseSvgPathData("M0,-10 A10,10 0 1,0 0,10 A10,10 0 1,0 0,-10 Z M6,-2 A8.5,8.5 0 1,1 6,2 A8.5,8.5 0 1,1 6,-2 Z"))
                {
                    moon.FillType = SKPathFillType.EvenOdd;
                    canvas.DrawPath(TransformPath(moon, cx, cy, scale), fill);
                }
                break;
            case 2: // lightning bolt
                DrawEmblemPath(canvas, "M2,-10 L-6,2 L-1,2 L-3,10 L6,-2 L1,-2 Z", cx, cy, scale, fill);
                break;
            case 3: // leaf
                DrawEmblemPath(canvas, "M0,10 C9,9 9,-9 0,-10 C-9,-9 -9,9 0,10 Z", cx, cy, scale, fill);
                break;
            case 4: // flame
                DrawEmblemPath(canvas, "M0,-10 C4,-5 8,-3 6,2 C5,4 5,8 0,10 C-1,6 -1,5 -4,3 C-8,0 -4,-6 0,-10 Z", cx, cy, scale, fill);
                break;
            case 5: // hexagon
                DrawEmblemPath(canvas, "M0,-10 L8.66,-5 L8.66,5 L0,10 L-8.66,5 L-8.66,-5 Z", cx, cy, scale, fill);
                break;
            case 6: // diamond
                DrawEmblemPath(canvas, "M0,-10 L7,0 L0,10 L-7,0 Z", cx, cy, scale, fill);
                break;
            case 7: // triangle
                DrawEmblemPath(canvas, "M0,-9 L8.5,7 L-8.5,7 Z", cx, cy, scale, fill);
                break;
            case 8: // plus
                DrawEmblemPath(canvas, "M-2,-10 L2,-10 L2,-2 L10,-2 L10,2 L2,2 L2,10 L-2,10 L-2,2 L-10,2 L-10,-2 L-2,-2 Z", cx, cy, scale, fill);
                break;
            case 9: // bullseye ring
                using (var ring = SKPath.ParseSvgPathData("M0,-10 A10,10 0 1,0 0,10 A10,10 0 1,0 0,-10 Z M0,-5 A5,5 0 1,1 0,5 A5,5 0 1,1 0,-5 Z"))
                {
                    ring.FillType = SKPathFillType.EvenOdd;
                    canvas.DrawPath(TransformPath(ring, cx, cy, scale), fill);
                }
                break;
            case 10: // sun
                canvas.DrawCircle(cx, cy, r * 0.42f, fill);
                for (var i = 0; i < 8; i++)
                {
                    var a = (float)(Math.PI / 4 * i);
                    var (vx, vy) = (MathF.Cos(a) * r * 0.72f, MathF.Sin(a) * r * 0.72f);
                    canvas.DrawLine(new SKPoint(cx + vx, cy + vy), new SKPoint(cx + vx * 1.55f, cy + vy * 1.55f), stroke);
                }
                break;
            case 11: // flower
                for (var i = 0; i < 5; i++)
                {
                    var a = (float)(Math.PI * 0.4 * i - Math.PI / 2);
                    var (px, py) = (MathF.Cos(a) * r * 0.55f, MathF.Sin(a) * r * 0.55f);
                    canvas.DrawCircle(cx + px, cy + py, r * 0.30f, fill);
                }
                canvas.DrawCircle(cx, cy, r * 0.32f, fill);
                break;
        }
    }

    private static SKPath TransformPath(SKPath path, float cx, float cy, float scale)
    {
        var copy = new SKPath(path);
        copy.Transform(SKMatrix.CreateScaleTranslation(scale, scale, cx, cy));
        return copy;
    }

    private static void DrawEmblemPath(SKCanvas canvas, string svgPath, float cx, float cy, float scale, SKPaint paint)
    {
        using var path = SKPath.ParseSvgPathData(svgPath);
        path.Transform(SKMatrix.CreateScaleTranslation(scale, scale, cx, cy));
        canvas.DrawPath(path, paint);
    }

    private void DrawSrvBackground(SKCanvas canvas, PosterTheme theme)
    {
        canvas.Clear(new SKColor(251, 249, 245));
        canvas.DrawRect(SKRect.Create(0, 0, Width, 10), new SKPaint { Color = new SKColor(4, 153, 159) });
        canvas.DrawRect(SKRect.Create(0, 10, Width, 4), new SKPaint { Color = new SKColor(253, 206, 32) });
        canvas.DrawRect(SKRect.Create(0, 14, Width, 4), new SKPaint { Color = new SKColor(4, 57, 137) });
    }

    private void DrawSrvContent(SKCanvas canvas, Poster poster, PosterTheme theme, Brand brand, SKImage? hero)
    {
        using var serifBold = SafeTypeface("Georgia", SKFontStyle.Bold);
        using var serifItalic = SafeTypeface("Georgia", SKFontStyle.BoldItalic);
        using var sansBold = SafeTypeface("Segoe UI", SKFontStyle.Bold);
        using var sans = SafeTypeface("Segoe UI", SKFontStyle.Normal);

        var navy = theme.Accent;
        var gold = theme.Secondary;
        var goldDark = new SKColor(200, 150, 8);
        var teal = new SKColor(4, 153, 159);
        var titleNavy = new SKColor(9, 46, 110);
        var dark = theme.Text;

        const float leftX = 70f;
        const float rightX = 540f;
        const float colW = 440f;

        // ---- Header emblems (three logos in fixed positions) ----------------------
        using (var lamp = LoadSrvBrandImage("lamp.png"))
        {
            if (lamp is not null)
            {
                var lampH = 168f;
                var lampW = lampH * lamp.Width / lamp.Height;
                canvas.DrawImage(lamp, SKRect.Create(128 - lampW / 2, 150 - lampH / 2, lampW, lampH), SKSamplingOptions.Default);
            }
            else
            {
                DrawSrvLampEmblem(canvas, 128, 150, 1f, navy, gold);
            }
        }

        using (var banner = LoadSrvBrandImage("banner.png"))
        {
            if (banner is not null)
            {
                var bannerH = 150f;
                var bannerW = bannerH * banner.Width / banner.Height;
                canvas.DrawImage(banner, SKRect.Create(540 - bannerW / 2, 150 - bannerH / 2, bannerW, bannerH), SKSamplingOptions.Default);
            }
            else
            {
                DrawSrvSchoolEmblem(canvas, 540, 150, navy, gold);
            }
        }

        using (var vp = LoadSrvBrandImage("vidyapeetham.png"))
        {
            if (vp is not null)
            {
                canvas.DrawImage(vp, SKRect.Create(948 - 66, 88, 132, 132), SKSamplingOptions.Default);
            }
            else
            {
                DrawSrvSchoolEmblem(canvas, 948, 152, navy, gold);
            }
        }

        // ---- Date banner (upper left) ---------------------------------------------
        using (var bannerPaint = new SKPaint { Color = gold, IsAntialias = true })
        using (var bannerStroke = new SKPaint { Color = goldDark, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true })
        using (var dateFont = new SKFont(sansBold, 23))
        using (var datePaint = new SKPaint { Color = navy, IsAntialias = true })
        using (var tabPaint = new SKPaint { Color = navy, IsAntialias = true })
        using (var goldPaint = new SKPaint { Color = goldDark, IsAntialias = true })
        {
            canvas.DrawRoundRect(SKRect.Create(leftX, 282, 440, 50), 10, 10, bannerPaint);
            canvas.DrawRoundRect(SKRect.Create(leftX, 282, 440, 50), 10, 10, bannerStroke);
            canvas.DrawRect(SKRect.Create(leftX, 282, 8, 50), tabPaint);

            var dateText = poster.EventDate.ToString("dddd, dd MMMM").ToUpperInvariant();
            var dateW = dateFont.MeasureText(dateText);
            canvas.DrawText(dateText, leftX + 220, 315, SKTextAlign.Center, dateFont, datePaint);
            DrawSrvDiamond(canvas, leftX + 224 - dateW / 2f - 22, 309, 5, goldPaint);
            DrawSrvDiamond(canvas, leftX + 224 + dateW / 2f + 22, 309, 5, goldPaint);
        }

        // ---- Kicker slogan (left, above the title) --------------------------------
        var (slogan, quote, attribution) = SrvCopy.For(poster);
        using (var kickerPaint = new SKPaint { Color = teal, IsAntialias = true })
        using (var kickerFont = new SKFont(sansBold, 19))
        {
            var kw = kickerFont.MeasureText(slogan);
            canvas.DrawText(slogan, leftX, 358, SKTextAlign.Left, kickerFont, kickerPaint);
            canvas.DrawRect(SKRect.Create(leftX + kw + 14, 352, 46, 3), new SKPaint { Color = gold });
        }

        // ---- Large bold event title (left) ----------------------------------------
        var headline = ShortTitle(poster);
        float titleSize = 64f;
        List<string>? titleLines = null;
        while (titleSize >= 32f)
        {
            using var probe = new SKFont(serifBold, titleSize);
            var lines = WrapText(probe, headline, colW);
            if (lines.Count <= 4 && lines.All(l => probe.MeasureText(l) <= colW))
            {
                titleLines = lines;
                break;
            }

            titleSize -= 3f;
        }

        if (titleLines == null)
        {
            titleSize = 32f;
            using var probe = new SKFont(serifBold, titleSize);
            titleLines = WrapText(probe, headline, colW).Take(4).ToList();
        }

        var y = 410f;
        using (var titlePaint = new SKPaint { Color = titleNavy, IsAntialias = true })
        using (var titleFont = new SKFont(serifBold, titleSize))
        {
            foreach (var line in titleLines)
            {
                canvas.DrawText(line, leftX, y, SKTextAlign.Left, titleFont, titlePaint);
                y += titleSize * 1.12f;
            }
        }

        using (var rulePaint = new SKPaint { Color = gold, IsAntialias = true })
        {
            canvas.DrawRect(SKRect.Create(leftX, y - 4, 88, 4), rulePaint);
        }

        // ---- Photorealistic hero illustration (right) ------------------------------
        DrawSrvHero(canvas, SKRect.Create(rightX, 320, 480, 420), hero, navy, teal, gold);

        // ---- Informational paragraph (full width) ----------------------------------
        var paragraph = !string.IsNullOrWhiteSpace(poster.Description)
            ? poster.Description
            : SrvCopy.ParagraphFor(poster);
        using (var labelPaint = new SKPaint { Color = teal, IsAntialias = true })
        using (var labelFont = new SKFont(sansBold, 19))
        using (var paraPaint = new SKPaint { Color = dark, IsAntialias = true })
        using (var paraFont = new SKFont(sans, 23))
        {
            canvas.DrawText("ABOUT THIS DAY", leftX, 772, SKTextAlign.Left, labelFont, labelPaint);
            canvas.DrawRect(SKRect.Create(leftX, 780, 60, 3), new SKPaint { Color = gold });

            var py = 816f;
            foreach (var line in WrapText(paraFont, paragraph, Width - leftX * 2).Take(3))
            {
                canvas.DrawText(line, leftX, py, SKTextAlign.Left, paraFont, paraPaint);
                py += 32f;
            }
        }

        // ---- Benefits / Importance infographic box with icons ----------------------
        const float boxX = 70f;
        const float boxW = 940f;
        var benefits = SrvCopy.BenefitsFor(poster);
        using (var boxPaint = new SKPaint { Color = new SKColor(250, 246, 236), IsAntialias = true })
        using (var boxStroke = new SKPaint { Color = navy, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true })
        {
            canvas.DrawRoundRect(SKRect.Create(boxX, 892, boxW, 146), 14, 14, boxPaint);
            canvas.DrawRoundRect(SKRect.Create(boxX, 892, boxW, 146), 14, 14, boxStroke);
        }

        using (var bHeadPaint = new SKPaint { Color = navy, IsAntialias = true })
        using (var bHeadFont = new SKFont(sansBold, 21))
        using (var goldPaint = new SKPaint { Color = goldDark, IsAntialias = true })
        {
            const string bHead = "BENEFITS & IMPORTANCE";
            canvas.DrawText(bHead, Width / 2f, 924, SKTextAlign.Center, bHeadFont, bHeadPaint);
            DrawSrvDiamond(canvas, Width / 2f - bHeadFont.MeasureText(bHead) / 2f - 22, 918, 5, goldPaint);
            DrawSrvDiamond(canvas, Width / 2f + bHeadFont.MeasureText(bHead) / 2f + 22, 918, 5, goldPaint);
        }

        var benefitColors = new[] { teal, navy, goldDark, teal, navy };
        using (var bFont = new SKFont(sans, 20))
        using (var bPaint = new SKPaint { Color = dark, IsAntialias = true })
        {
            var cols = new[] { new[] { 0, 1, 2 }, new[] { 3, 4 } };
            var bx = new[] { 100f, 560f };
            for (var col = 0; col < 2; col++)
            {
                var py = 952f;
                foreach (var idx in cols[col])
                {
                    using var iconFill = new SKPaint { Color = benefitColors[idx % benefitColors.Length], IsAntialias = true };
                    DrawSrvBenefitIcon(canvas, bx[col] + 14, py - 7, idx, iconFill);
                    canvas.DrawText(benefits[idx], bx[col] + 40, py, SKTextAlign.Left, bFont, bPaint);
                    py += 38f;
                }
            }
        }

        // ---- Inspirational quote box ------------------------------------------------
        using (var qBoxPaint = new SKPaint { Color = new SKColor(253, 246, 228), IsAntialias = true })
        using (var qBoxStroke = new SKPaint { Color = goldDark, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true })
        {
            canvas.DrawRoundRect(SKRect.Create(boxX, 1052, boxW, 100), 14, 14, qBoxPaint);
            canvas.DrawRoundRect(SKRect.Create(boxX, 1052, boxW, 100), 14, 14, qBoxStroke);
        }

        using (var qMarkPaint = new SKPaint { Color = gold, IsAntialias = true })
        using (var qMarkFont = new SKFont(serifBold, 42))
        {
            canvas.DrawText("\u201C", 92, 1096, SKTextAlign.Left, qMarkFont, qMarkPaint);
        }

        using (var qQuotePaint = new SKPaint { Color = new SKColor(62, 58, 54), IsAntialias = true })
        using (var qQuoteFont = new SKFont(serifItalic, 19))
        {
            var qy = 1096f;
            foreach (var line in WrapText(qQuoteFont, quote, 850).Take(2))
            {
                canvas.DrawText(line, 130, qy, SKTextAlign.Left, qQuoteFont, qQuotePaint);
                qy += 26f;
            }
        }

        using (var qAttPaint = new SKPaint { Color = navy, IsAntialias = true })
        using (var qAttFont = new SKFont(serifItalic, 15))
        {
            canvas.DrawText("\u2014 " + attribution, boxX + boxW - 28, 1140, SKTextAlign.Right, qAttFont, qAttPaint);
        }

        // ---- Our Values, Our Strength (six colourful icons) -------------------------
        if (brand.ShowValues && brand.Values.Length > 0)
        {
            using (var vHeadPaint = new SKPaint { Color = navy, IsAntialias = true })
            using (var vHeadFont = new SKFont(sansBold, 20))
            using (var goldPaint = new SKPaint { Color = goldDark, IsAntialias = true })
            {
                const string vHead = "OUR VALUES, OUR STRENGTH";
                canvas.DrawText(vHead, Width / 2f, 1184, SKTextAlign.Center, vHeadFont, vHeadPaint);
                DrawSrvDiamond(canvas, Width / 2f - vHeadFont.MeasureText(vHead) / 2f - 20, 1178, 5, goldPaint);
                DrawSrvDiamond(canvas, Width / 2f + vHeadFont.MeasureText(vHead) / 2f + 20, 1178, 5, goldPaint);
            }

            var words = brand.Values.Select(v => v.ToUpperInvariant()).ToArray();
            var valueColors = new[]
            {
                new SKColor(4, 153, 159), new SKColor(9, 46, 110), new SKColor(226, 166, 8),
                new SKColor(243, 127, 32), new SKColor(64, 151, 75), new SKColor(213, 63, 89)
            };
            const float spacing = 156.7f;
            const float circleY = 1210f;
            using var vLabelFont = new SKFont(sansBold, 12.5f);
            using var vLabelPaint = new SKPaint { Color = titleNavy, IsAntialias = true };
            for (var i = 0; i < words.Length; i++)
            {
                var cx = leftX + 78f + i * spacing;
                using var fill = new SKPaint { Color = valueColors[i % valueColors.Length], IsAntialias = true };
                canvas.DrawCircle(cx, circleY, 19, fill);
                using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
                DrawSrvValueIcon(canvas, cx, circleY, i, white);
                canvas.DrawText(words[i], cx, circleY + 36, SKTextAlign.Center, vLabelFont, vLabelPaint);
            }
        }

        // ---- Footer navy band ---------------------------------------------------------
        const float footerTop = 1250f;
        canvas.DrawRect(SKRect.Create(0, footerTop, Width, SrvHeight - footerTop), new SKPaint { Color = navy });
        using (var topRule = new SKPaint { Color = gold })
        {
            canvas.DrawRect(SKRect.Create(0, footerTop, Width, 4), topRule);
        }

        // 60 Years SRKV logo anchored bottom-left (real crest image when available).
        using (var logo = LoadSrvCrestImage())
        {
            if (logo is not null)
            {
                const float logoSize = 176f;
                canvas.DrawImage(logo, SKRect.Create(70f, SrvHeight - logoSize - 30f, logoSize, logoSize), SKSamplingOptions.Default);
            }
            else
            {
                DrawSrvYearsBadge(canvas, 136, 1305, navy, gold);
            }
        }

        var schoolName = string.IsNullOrWhiteSpace(brand.Name) ? "YOUR ORGANIZATION" : brand.Name.ToUpperInvariant();
        var cityName = string.IsNullOrWhiteSpace(brand.City) ? string.Empty : brand.City.ToUpperInvariant();
        var tagline = string.IsNullOrWhiteSpace(brand.Tagline)
            ? "Education with Values | Discipline in Life | Excellence in All"
            : brand.Tagline;
        var socials = $"{brand.Facebook}    \u2022    {brand.Instagram}";
        var phones = string.Join("    \u2022    ", brand.Phones.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // Footer text is centred in the space right of the badge.
        const float footerCenterX = 540f;
        using (var nameFont = new SKFont(sansBold, 22))
        using (var namePaint = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            canvas.DrawText($"{schoolName}, {cityName}", footerCenterX, 1278, SKTextAlign.Center, nameFont, namePaint);
        }

        using (var tagFont = new SKFont(sans, 14))
        using (var tagPaint = new SKPaint { Color = new SKColor(255, 255, 255, 225), IsAntialias = true })
        {
            canvas.DrawText(tagline, footerCenterX, 1300, SKTextAlign.Center, tagFont, tagPaint);
        }

        using (var socFont = new SKFont(sans, 13))
        using (var socPaint = new SKPaint { Color = new SKColor(255, 255, 255, 235), IsAntialias = true })
        {
            var socW = socFont.MeasureText(socials);
            DrawSrvSocialIcon(canvas, footerCenterX - socW / 2f, 1315, "fb", gold, navy);
            canvas.DrawText(socials, footerCenterX, 1320, SKTextAlign.Center, socFont, socPaint);
        }

        using (var phFont = new SKFont(sans, 13))
        using (var phPaint = new SKPaint { Color = new SKColor(255, 255, 255, 235), IsAntialias = true })
        {
            canvas.DrawText(phones, footerCenterX, 1339, SKTextAlign.Center, phFont, phPaint);
        }

    }

    /// <summary>Draws the hero illustration panel on the right, cover-cropping the
    /// real event photo when available, otherwise a stylised lamp emblem.</summary>
    private void DrawSrvHero(SKCanvas canvas, SKRect panel, SKImage? hero, SKColor navy, SKColor teal, SKColor gold)
    {
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 26), IsAntialias = true };
        canvas.DrawRoundRect(SKRect.Create(panel.Left + 5, panel.Top + 8, panel.Width, panel.Height), 20, 20, shadow);

        var heroPanelBuilder = new SKPathBuilder();
        heroPanelBuilder.AddRoundRect(panel, 20f, 20f, SKPathDirection.Clockwise);
        using var path = heroPanelBuilder.Detach();

        if (hero is not null)
        {
            canvas.Save();
            canvas.ClipPath(path, SKClipOperation.Intersect, true);
            var scale = Math.Max(panel.Width / hero.Width, panel.Height / hero.Height);
            var w = hero.Width * scale;
            var h = hero.Height * scale;
            var src = new SKRect(panel.Left + (panel.Width - w) / 2f, panel.Top + (panel.Height - h) / 2f, panel.Left + (panel.Width - w) / 2f + w, panel.Top + (panel.Height - h) / 2f + h);
            canvas.DrawImage(hero, src, SKSamplingOptions.Default);
            canvas.Restore();
        }
        else
        {
            using var fill = new SKPaint { Color = new SKColor(229, 245, 246), IsAntialias = true };
            canvas.Save();
            canvas.ClipPath(path, SKClipOperation.Intersect, true);
            canvas.DrawRect(panel, fill);
            using var rays = new SKPaint { Color = new SKColor(4, 153, 159, 40), IsAntialias = true };
            canvas.DrawCircle(panel.Left + panel.Width / 2, panel.Top + panel.Height / 2, panel.Width * 0.32f, rays);
            DrawSrvLampEmblem(canvas, panel.Left + panel.Width / 2, panel.Top + panel.Height / 2 + 8, 1.5f, navy, gold);
            canvas.Restore();
        }

        using var border = new SKPaint { Color = teal, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        canvas.DrawRoundRect(panel, 20, 20, border);
    }

    /// <summary>Procedural deepam (lamp + flame) used as a fallback when no lamp logo file is present.</summary>
    private static void DrawSrvLampEmblem(SKCanvas canvas, float cx, float cy, float scale, SKColor navy, SKColor gold)
    {
        var s = scale * 22f;

        using var ring = new SKPaint { Color = gold, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        canvas.DrawCircle(cx, cy, 34 * scale + 6, ring);

        using var bowl = new SKPaint { Color = gold, IsAntialias = true };
        var bowlBuilder = new SKPathBuilder();
        bowlBuilder.MoveTo(cx - 30 * s, cy - 8 * s);
        bowlBuilder.LineTo(cx + 30 * s, cy - 8 * s);
        bowlBuilder.CubicTo(cx + 26 * s, cy + 14 * s, cx + 16 * s, cy + 26 * s, cx, cy + 26 * s);
        bowlBuilder.CubicTo(cx - 16 * s, cy + 26 * s, cx - 26 * s, cy + 14 * s, cx - 30 * s, cy - 8 * s);
        bowlBuilder.Close();
        using var bowlPath = bowlBuilder.Detach();
        canvas.DrawPath(bowlPath, bowl);

        using var flame = new SKPaint { Color = new SKColor(255, 158, 27), IsAntialias = true };
        var flameBuilder = new SKPathBuilder();
        flameBuilder.MoveTo(cx, cy - 62 * s);
        flameBuilder.CubicTo(cx + 14 * s, cy - 34 * s, cx + 13 * s, cy - 12 * s, cx, cy - 2 * s);
        flameBuilder.CubicTo(cx - 13 * s, cy - 12 * s, cx - 14 * s, cy - 34 * s, cx, cy - 62 * s);
        flameBuilder.Close();
        using var flamePath = flameBuilder.Detach();
        canvas.DrawPath(flamePath, flame);

        using var wick = new SKPaint { Color = navy, IsAntialias = true };
        canvas.DrawCircle(cx, cy - 2 * s, 3.5f * s, wick);
    }

    /// <summary>Procedural school crest fallback: gold double ring + lamp monogram.</summary>
    private static void DrawSrvSchoolEmblem(SKCanvas canvas, float cx, float cy, SKColor navy, SKColor gold)
    {
        using var ring = new SKPaint { Color = gold, Style = SKPaintStyle.Stroke, StrokeWidth = 5, IsAntialias = true };
        canvas.DrawCircle(cx, cy, 74, ring);
        using var ring2 = new SKPaint { Color = gold, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawCircle(cx, cy, 66, ring2);
        using var fill = new SKPaint { Color = navy, IsAntialias = true };
        canvas.DrawCircle(cx, cy, 60, fill);
        DrawSrvLampEmblem(canvas, cx, cy + 4, 0.62f, navy, gold);
    }

    /// <summary>Gold circular "60 Years SRKV" badge anchored to the bottom-left footer.</summary>
    private static void DrawSrvYearsBadge(SKCanvas canvas, float cx, float cy, SKColor navy, SKColor gold)
    {
        const float r = 52f;
        using var fill = new SKPaint { Color = gold, IsAntialias = true };
        canvas.DrawCircle(cx, cy, r, fill);
        using var ring = new SKPaint { Color = navy, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        canvas.DrawCircle(cx, cy, r, ring);
        using var ring2 = new SKPaint { Color = navy, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawCircle(cx, cy, r - 4, ring2);

        using var sixtyFont = new SKFont(SafeTypeface("Georgia", SKFontStyle.Bold), 34);
        using var sixtyPaint = new SKPaint { Color = navy, IsAntialias = true };
        canvas.DrawText("60", cx, cy - 4, SKTextAlign.Center, sixtyFont, sixtyPaint);

        using var yearsFont = new SKFont(SafeTypeface("Segoe UI", SKFontStyle.Bold), 12);
        canvas.DrawText("YEARS", cx, cy + 18, SKTextAlign.Center, yearsFont, sixtyPaint);

        using var srvFont = new SKFont(SafeTypeface("Segoe UI", SKFontStyle.Bold), 14);
        canvas.DrawText("SRKV", cx, cy + 36, SKTextAlign.Center, srvFont, sixtyPaint);
    }

    /// <summary>Draws a coloured circular icon with a white glyph for the Benefits box.</summary>
    private static void DrawSrvBenefitIcon(SKCanvas canvas, float cx, float cy, int index, SKPaint fill)
    {
        canvas.DrawCircle(cx, cy, 13, fill);
        using var whiteFill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var whiteStroke = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

        switch (index % 5)
        {
            case 0: // check
                {
                    var checkBuilder = new SKPathBuilder();
                    checkBuilder.MoveTo(cx - 4.5f, cy);
                    checkBuilder.LineTo(cx - 1f, cy + 4f);
                    checkBuilder.LineTo(cx + 5f, cy - 4f);
                    using var path = checkBuilder.Detach();
                    canvas.DrawPath(path, whiteStroke);
                }

                break;
            case 1: // star
                {
                    var starBuilder = new SKPathBuilder();
                    for (var k = 0; k < 5; k++)
                    {
                        var outer = Math.PI / 2 + k * 2 * Math.PI / 5;
                        var inner = Math.PI / 2 + k * 2 * Math.PI / 5 + Math.PI / 5;
                        var ox = cx + 6.5f * Math.Cos(outer);
                        var oy = cy + 6.5f * Math.Sin(outer);
                        var ix = cx + 3f * Math.Cos(inner);
                        var iy = cy + 3f * Math.Sin(inner);
                        if (k == 0)
                        {
                            starBuilder.MoveTo((float)ox, (float)oy);
                        }
                        else
                        {
                            starBuilder.LineTo((float)ox, (float)oy);
                        }

                        starBuilder.LineTo((float)ix, (float)iy);
                    }

                    starBuilder.Close();
                    using var star = starBuilder.Detach();
                    canvas.DrawPath(star, whiteFill);
                }

                break;
            case 2: // heart
                {
                    var heartBuilder = new SKPathBuilder();
                    heartBuilder.MoveTo(cx, cy + 5f);
                    heartBuilder.CubicTo(cx - 9f, cy - 3f, cx - 5f, cy - 9f, cx, cy - 3f);
                    heartBuilder.CubicTo(cx + 5f, cy - 9f, cx + 9f, cy - 3f, cx, cy + 5f);
                    heartBuilder.Close();
                    using var heart = heartBuilder.Detach();
                    canvas.DrawPath(heart, whiteFill);
                }

                break;
            case 3: // diamond
                canvas.Save();
                canvas.Translate(cx, cy);
                canvas.RotateDegrees(45);
                canvas.DrawRect(SKRect.Create(-4.5f, -4.5f, 9, 9), whiteFill);
                canvas.Restore();
                break;
            default: // sun / spark
                canvas.DrawCircle(cx, cy, 4, whiteFill);
                using (var ring = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true })
                {
                    canvas.DrawCircle(cx, cy, 7, ring);
                }

                break;
        }
    }

    /// <summary>Small geometric icons for the school values row.</summary>
    private static void DrawSrvValueIcon(SKCanvas canvas, float cx, float cy, int index, SKPaint paint)
    {
        switch (index % 5)
        {
            case 0:
                canvas.DrawCircle(cx, cy, 8, paint);
                break;
            case 1:
                DrawSrvDiamond(canvas, cx, cy, 9, paint);
                break;
            case 2:
                canvas.DrawRect(SKRect.Create(cx - 7, cy - 7, 14, 14), paint);
                break;
            case 3:
                {
                    var triBuilder = new SKPathBuilder();
                    triBuilder.MoveTo(cx, cy - 9);
                    triBuilder.LineTo(cx + 8, cy + 6);
                    triBuilder.LineTo(cx - 8, cy + 6);
                    triBuilder.Close();
                    using var tri = triBuilder.Detach();
                    canvas.DrawPath(tri, paint);
                }
                break;
            default:
                canvas.DrawRoundRect(SKRect.Create(cx - 8, cy - 8, 16, 16), 4, 4, paint);
                break;
        }
    }

    /// <summary>Draws a tiny gold Facebook/Instagram glyph before the social handles.</summary>
    private static void DrawSrvSocialIcon(SKCanvas canvas, float x, float cy, string kind, SKColor gold, SKColor navy)
    {
        using var paint = new SKPaint { Color = gold, IsAntialias = true };
        var rect = SKRect.Create(x, cy - 7, 15, 15);
        if (kind == "fb")
        {
            canvas.DrawRoundRect(rect, 3, 3, paint);
            using var fPaint = new SKPaint { Color = navy, IsAntialias = true };
            using var fFont = new SKFont(SafeTypeface("Segoe UI", SKFontStyle.Bold), 11);
            canvas.DrawText("f", x + 7.5f, cy + 4, SKTextAlign.Center, fFont, fPaint);
        }
        else
        {
            using var stroke = new SKPaint { Color = gold, Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f, IsAntialias = true };
            canvas.DrawRoundRect(rect, 3, 3, stroke);
            canvas.DrawCircle(x + 7.5f, cy - 1, 3.2f, stroke);
            canvas.DrawCircle(x + 11.5f, cy - 5, 1.3f, paint);
        }
    }

    private static void DrawSrvDiamond(SKCanvas canvas, float x, float y, float half, SKPaint paint)
    {
        canvas.Save();
        canvas.Translate(x, y);
        canvas.RotateDegrees(45);
        canvas.DrawRect(SKRect.Create(-half, -half, half * 2, half * 2), paint);
        canvas.Restore();
    }

    /// <summary>Derives a short, punchy headline for the poster from the event's wiki
    /// page title when available, otherwise from the leading words of the event text.</summary>
    private static string ShortTitle(Poster poster)
    {
        var url = poster.Events.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Url))?.Url;
        if (!string.IsNullOrWhiteSpace(url))
        {
            const string marker = "/wiki/";
            var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var slug = url[(idx + marker.Length)..].Split('#', '?')[0].TrimEnd('/');
                var label = Regex.Replace(slug, "_+", " ").Trim();
                if (label.Length >= 3 && label.Length <= 70)
                {
                    return label.ToUpperInvariant();
                }
            }
        }

        var words = poster.EventTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 8)
        {
            return string.Join(" ", words.Take(5)).TrimEnd(':', ',', ';', '.', '-').ToUpperInvariant();
        }

        return poster.EventTitle.ToUpperInvariant();
    }

    private static string Spaced(string value)
    {
        return string.Join(" ", value.ToCharArray());
    }

    private static List<string> WrapText(SKFont font, string text, float maxWidth)
    {
        var result = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();

        foreach (var word in words)
        {
            var probe = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(probe) <= maxWidth || current.Length == 0)
            {
                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }
            else
            {
                result.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    // ---------------------------------------------------------------- AI image

    private async Task<SKImage?> FetchAiBackgroundAsync(Poster poster, CancellationToken ct)
    {
        var endpoint = (await _settings.GetAsync("ai.endpoint", "https://api.openai.com/v1"))!.TrimEnd('/');
        var apiKey = await _settings.GetAsync("ai.apiKey");
        var model = await _settings.GetAsync("ai.imageModel", "dall-e-3");

        var prompt = $"A beautiful, vibrant editorial poster background illustration inspired by: \"{poster.EventTitle}\". " +
                     $"No text, no letters, no words, no watermark. {Width}x{Height} canvas, rich colors, subtle details, plenty of negative space in the center-left for text.";

        var payload = new
        {
            model,
            prompt,
            n = 1,
            size = "1792x1024",
            response_format = "b64_json"
        };

        var http = _httpFactory.CreateClient("ai");
        var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/images/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AI image API failed ({response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data")[0];

        if (data.TryGetProperty("b64_json", out var b64))
        {
            return SKImage.FromEncodedData(Convert.FromBase64String(b64.GetString()!));
        }

        if (data.TryGetProperty("url", out var url))
        {
            using var imgResponse = await http.GetAsync(url.GetString(), ct);
            var bytes = await imgResponse.Content.ReadAsByteArrayAsync(ct);
            return SKImage.FromEncodedData(bytes);
        }

        throw new InvalidOperationException("AI image API returned an unexpected payload.");
    }

    // ---------------------------------------------------------------- save

    /// <summary>Decodes a per-template uploaded logo (PNG/JPG/WEBP bytes) for overlay.</summary>
    private async Task<SKBitmap?> LoadTemplateLogoAsync(byte[]? logoBytes, CancellationToken ct)
    {
        if (logoBytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            await using var stream = new MemoryStream(logoBytes);
            return SKBitmap.Decode(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not decode uploaded template logo.");
            return null;
        }
    }

    /// <summary>Loads the tenant's uploaded logo (if any) for overlay onto generated posters.</summary>
    private async Task<SKBitmap?> LoadTenantLogoAsync(int tenantId, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var relative = await db.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => t.LogoPath)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrWhiteSpace(relative))
            {
                return null;
            }

            var wwwRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var full = Path.Combine(wwwRoot, relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                return null;
            }

            await using var stream = File.OpenRead(full);
            return SKBitmap.Decode(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load logo for tenant {TenantId}.", tenantId);
            return null;
        }
    }

    private static void DrawTenantLogo(SKCanvas canvas, SKBitmap logo, string? position = null)
    {
        const float pad = 40f;
        const float targetHeight = 110f;
        position ??= "top-right";

        if (position.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var scale = targetHeight / logo.Height;
        var scaledWidth = logo.Width * scale;

        float left;
        float top;
        var rightAligned = position.Contains("right", StringComparison.OrdinalIgnoreCase);
        var bottomAligned = position.Contains("bottom", StringComparison.OrdinalIgnoreCase);
        var centeredVertically = position.Contains("middle", StringComparison.OrdinalIgnoreCase)
                                 || position.Equals("center", StringComparison.OrdinalIgnoreCase);
        var centeredHorizontally = position.Contains("top-center", StringComparison.OrdinalIgnoreCase)
                                   || position.Contains("bottom-center", StringComparison.OrdinalIgnoreCase)
                                   || position.Equals("center", StringComparison.OrdinalIgnoreCase);

        if (centeredHorizontally)
        {
            left = (Width - scaledWidth) / 2f;
        }
        else if (rightAligned)
        {
            left = Width - pad - scaledWidth;
        }
        else
        {
            left = pad;
        }

        if (bottomAligned)
        {
            top = Height - pad - targetHeight;
        }
        else if (centeredVertically)
        {
            top = (Height - targetHeight) / 2f;
        }
        else
        {
            top = pad;
        }

        var dest = new SKRect(left, top, left + scaledWidth, top + targetHeight);

        // Soft white backing keeps dark logos readable on dark posters.
        using var backing = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, 200)
        };
        canvas.DrawRoundRect(
            dest.Left - 10, dest.Top - 10, dest.Width + 20, dest.Height + 20, 14, 14, backing);

        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(logo, dest, SKSamplingOptions.Default, paint);
    }

    /// <summary>Previews are throwaway render caches; delete ones older than 24h. Best effort.</summary>
    private static void CleanupOldPreviews(string previewDir)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var file in Directory.EnumerateFiles(previewDir, "preview_*.png"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                    // A concurrent request may still be streaming this file; skip it.
                }
            }
        }
        catch
        {
            // Cleanup must never break rendering.
        }
    }

    private string SaveSurface(SKSurface surface, Poster poster, bool preview = false)
    {
        if (preview)
        {
            var previewDir = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "posters", "previews");
            Directory.CreateDirectory(previewDir);
            var previewPath = Path.Combine(previewDir, $"preview_{Guid.NewGuid().ToString("N")[..10]}.png");
            using (var pv = surface.Snapshot())
            using (var pdata = pv.Encode(SKEncodedImageFormat.Png, 92))
            using (var pstream = File.Create(previewPath))
            {
                pdata.SaveTo(pstream);
            }

            CleanupOldPreviews(previewDir);
            return "/posters/previews/" + Path.GetFileName(previewPath);
        }

        var dateFolder = poster.EventDate.ToString("yyyy");
        var relativeDir = Path.Combine("posters", dateFolder);
        var wwwRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var dir = Path.Combine(wwwRoot, relativeDir);
        Directory.CreateDirectory(dir);

        var fileName = $"{poster.EventDate:yyyyMMdd}_{poster.Id:00000}.png";
        var fullPath = Path.Combine(dir, fileName);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        poster.ImageBytes = data.ToArray();
        using var stream = File.Create(fullPath);
        data.SaveTo(stream);

        return "/" + relativeDir.Replace('\\', '/') + "/" + fileName;
    }
}

public sealed record Brand
{
    public string Name { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Tagline { get; init; } = string.Empty;
    public string Facebook { get; init; } = string.Empty;
    public string Instagram { get; init; } = string.Empty;
    public string Phones { get; init; } = string.Empty;
    public bool ShowValues { get; init; }
    public string[] Values { get; init; } = Array.Empty<string>();
}

public sealed record PosterTheme
{
    public SKColor[] Gradient { get; init; } = Array.Empty<SKColor>();
    public SKColor Accent { get; init; }
    public SKColor Secondary { get; init; }
    public SKColor Text { get; init; }
    public SKColor Muted { get; init; }
    public SKColor Faint { get; init; }
    public SKColor Tag { get; init; }
    public SKColor Shadow { get; init; }
    public SKColor Watermark { get; init; }
    public SKColor Noise { get; init; }
    public SKColor Frame { get; init; }
    public SKColor Glow { get; init; }
    public SKColor ChipText { get; init; }
    public bool IsLight { get; init; }
    public bool IsColorful { get; init; }
    public bool IsSrv { get; init; }

    public static readonly string[] SignatureModes =
    {
        "sunset", "ocean", "forest", "royal", "neon", "espresso", "teal", "crimson",
        "gold", "plum", "jade", "merlot", "charcoal", "midnight", "mint", "blush",
        "cream", "slate", "citrus"
    };

    private readonly record struct SignaturePalette(SKColor G0, SKColor G1, SKColor Accent, bool Light);

    private static readonly Dictionary<string, SignaturePalette> SignatureThemes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "sunset", new(new SKColor(255, 84, 56), new SKColor(255, 44, 103), new SKColor(255, 209, 102), false) },
            { "ocean",  new(new SKColor(6, 36, 70), new SKColor(13, 122, 166), new SKColor(94, 234, 212), false) },
            { "forest", new(new SKColor(13, 43, 28), new SKColor(32, 112, 64), new SKColor(252, 211, 77), false) },
            { "royal",  new(new SKColor(21, 25, 84), new SKColor(89, 29, 135), new SKColor(245, 197, 24), false) },
            { "neon",   new(new SKColor(13, 13, 28), new SKColor(46, 18, 86), new SKColor(34, 211, 238), false) },
            { "espresso", new(new SKColor(32, 23, 16), new SKColor(86, 54, 30), new SKColor(242, 199, 143), false) },
            { "teal",   new(new SKColor(2, 45, 53), new SKColor(16, 117, 128), new SKColor(255, 232, 163), false) },
            { "crimson", new(new SKColor(46, 8, 20), new SKColor(130, 28, 60), new SKColor(255, 202, 202), false) },
            { "gold",   new(new SKColor(46, 36, 8), new SKColor(126, 96, 20), new SKColor(253, 230, 138), false) },
            { "plum",   new(new SKColor(42, 10, 66), new SKColor(112, 30, 112), new SKColor(249, 168, 212), false) },
            { "jade",   new(new SKColor(12, 28, 26), new SKColor(18, 112, 82), new SKColor(167, 243, 208), false) },
            { "merlot", new(new SKColor(36, 10, 18), new SKColor(98, 24, 42), new SKColor(254, 202, 202), false) },
            { "charcoal", new(new SKColor(18, 18, 20), new SKColor(58, 58, 62), new SKColor(250, 204, 21), false) },
            { "midnight", new(new SKColor(10, 16, 40), new SKColor(30, 58, 110), new SKColor(245, 158, 11), false) },
            { "mint",   new(new SKColor(230, 250, 240), new SKColor(208, 240, 224), new SKColor(15, 118, 110), true) },
            { "blush",  new(new SKColor(255, 241, 244), new SKColor(255, 227, 233), new SKColor(190, 93, 115), true) },
            { "cream",  new(new SKColor(252, 247, 238), new SKColor(242, 233, 218), new SKColor(161, 98, 7), true) },
            { "slate",  new(new SKColor(245, 247, 250), new SKColor(226, 232, 240), new SKColor(51, 65, 85), true) },
            { "citrus", new(new SKColor(255, 250, 230), new SKColor(254, 240, 138), new SKColor(202, 138, 4), true) }
        };

    public static PosterTheme From(SKColor[] palette, string mode)
    {
        if (mode == "srv")
        {
            return new PosterTheme
            {
                Gradient = new[] { new SKColor(243, 240, 235), new SKColor(243, 240, 235) },
                Accent = new SKColor(4, 57, 137),
                Secondary = new SKColor(253, 206, 32),
                Text = new SKColor(45, 44, 40),
                Muted = new SKColor(70, 66, 60, 240),
                Faint = new SKColor(120, 116, 110, 200),
                Tag = new SKColor(4, 57, 137),
                Shadow = new SKColor(0, 0, 0, 24),
                Watermark = new SKColor(253, 206, 32, 20),
                Noise = new SKColor(20, 20, 20, 12),
                Frame = new SKColor(253, 206, 32),
                Glow = new SKColor(253, 206, 32, 22),
                ChipText = SKColors.White,
                IsLight = true,
                IsSrv = true
            };
        }

        if (SignatureThemes.TryGetValue(mode, out var sig))
        {
            return new PosterTheme
            {
                Gradient = new[] { sig.G0, sig.G1 },
                Accent = sig.Accent,
                Secondary = sig.Accent,
                Text = sig.Light ? new SKColor(32, 28, 22) : SKColors.White,
                Muted = sig.Light ? new SKColor(70, 64, 54, 235) : new SKColor(255, 255, 255, 222),
                Faint = sig.Light ? new SKColor(110, 102, 90, 190) : new SKColor(255, 255, 255, 140),
                Tag = sig.Light ? new SKColor(38, 34, 28, 255) : new SKColor(255, 255, 255, 235),
                Shadow = new SKColor(0, 0, 0, sig.Light ? (byte)26 : (byte)70),
                Watermark = new SKColor(sig.Accent.Red, sig.Accent.Green, sig.Accent.Blue, sig.Light ? (byte)16 : (byte)38),
                Noise = new SKColor(sig.Light ? (byte)20 : (byte)255, sig.Light ? (byte)20 : (byte)255, sig.Light ? (byte)20 : (byte)255, sig.Light ? (byte)16 : (byte)22),
                Frame = sig.Light ? new SKColor(40, 36, 30, 52) : new SKColor(255, 255, 255, 70),
                Glow = new SKColor(sig.Accent.Red, sig.Accent.Green, sig.Accent.Blue, sig.Light ? (byte)26 : (byte)30),
                ChipText = SKColors.White,
                IsLight = sig.Light
            };
        }

        if (mode == "colorful")
        {
            return new PosterTheme
            {
                Gradient = new[] { palette[0], palette[1] },
                Accent = palette[2],
                Secondary = palette[3],
                Text = SKColors.White,
                Muted = new SKColor(255, 255, 255, 238),
                Faint = new SKColor(255, 255, 255, 175),
                Tag = new SKColor(255, 255, 255, 252),
                Shadow = new SKColor(0, 0, 0, 90),
                Watermark = new SKColor(255, 255, 255, 26),
                Noise = new SKColor(255, 255, 255, 20),
                Frame = new SKColor(255, 255, 255, 70),
                Glow = new SKColor(255, 255, 255, 30),
                ChipText = SKColors.White,
                IsLight = false,
                IsColorful = true
            };
        }

        if (mode == "light")
        {
            return new PosterTheme
            {
                Gradient = new[] { palette[0], palette[1] },
                Accent = palette[2],
                Secondary = palette[2],
                Text = new SKColor(32, 28, 22),
                Muted = new SKColor(70, 64, 54, 235),
                Faint = new SKColor(110, 102, 90, 190),
                Tag = new SKColor(38, 34, 28, 255),
                Shadow = new SKColor(0, 0, 0, 26),
                Watermark = new SKColor(palette[2].Red, palette[2].Green, palette[2].Blue, 16),
                Noise = new SKColor(20, 20, 20, 16),
                Frame = new SKColor(40, 36, 30, 52),
                Glow = new SKColor(palette[2].Red, palette[2].Green, palette[2].Blue, 26),
                ChipText = SKColors.White,
                IsLight = true
            };
        }

        return new PosterTheme
        {
            Gradient = new[] { palette[0], palette[1] },
            Accent = palette[2],
            Secondary = palette[2],
            Text = SKColors.White,
            Muted = new SKColor(255, 255, 255, 222),
            Faint = new SKColor(255, 255, 255, 140),
            Tag = new SKColor(255, 255, 255, 235),
            Shadow = new SKColor(0, 0, 0, 70),
            Watermark = new SKColor(palette[2].Red, palette[2].Green, palette[2].Blue, 38),
            Noise = new SKColor(255, 255, 255, 22),
            Frame = new SKColor(255, 255, 255, 70),
            Glow = new SKColor(255, 255, 255, 26),
            ChipText = SKColors.White,
            IsLight = false
        };
    }

    public PosterTheme WithAccent(string hex)
    {
        if (!TryParseHex(hex, out var color))
        {
            return this;
        }

        return this with
        {
            Accent = color,
            Secondary = color,
            Tag = color,
            Watermark = new SKColor(color.Red, color.Green, color.Blue, (byte)(IsLight ? 18 : 38)),
            Glow = new SKColor(color.Red, color.Green, color.Blue, (byte)(IsLight ? 26 : 30))
        };
    }

    private static bool TryParseHex(string? hex, out SKColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6)
        {
            return false;
        }

        if (!byte.TryParse(h.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(h.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(h.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return false;
        }

        color = new SKColor(r, g, b);
        return true;
    }
}

public static class PosterPalettes
{
    private static readonly SKColor[][] DarkPalettes =
    {
        new[] { new SKColor(18, 24, 68), new SKColor(83, 32, 122), new SKColor(255, 121, 168) },
        new[] { new SKColor(4, 54, 91), new SKColor(24, 128, 121), new SKColor(255, 209, 102) },
        new[] { new SKColor(41, 12, 62), new SKColor(120, 40, 110), new SKColor(255, 170, 110) },
        new[] { new SKColor(12, 44, 74), new SKColor(20, 120, 150), new SKColor(130, 224, 200) },
        new[] { new SKColor(54, 12, 26), new SKColor(128, 30, 70), new SKColor(255, 200, 120) },
        new[] { new SKColor(20, 30, 48), new SKColor(70, 70, 130), new SKColor(190, 150, 255) },
        new[] { new SKColor(8, 62, 46), new SKColor(30, 130, 90), new SKColor(230, 230, 140) },
        new[] { new SKColor(40, 20, 10), new SKColor(130, 70, 30), new SKColor(255, 190, 100) }
    };

    private static readonly SKColor[][] LightPalettes =
    {
        new[] { new SKColor(250, 248, 244), new SKColor(240, 236, 228), new SKColor(214, 64, 69) },
        new[] { new SKColor(249, 250, 247), new SKColor(235, 240, 236), new SKColor(15, 113, 115) },
        new[] { new SKColor(248, 247, 250), new SKColor(233, 229, 244), new SKColor(94, 75, 155) },
        new[] { new SKColor(250, 247, 242), new SKColor(238, 232, 222), new SKColor(201, 123, 35) },
        new[] { new SKColor(247, 250, 248), new SKColor(228, 238, 233), new SKColor(46, 125, 91) },
        new[] { new SKColor(250, 246, 248), new SKColor(240, 228, 234), new SKColor(177, 74, 106) },
        new[] { new SKColor(244, 248, 250), new SKColor(225, 236, 242), new SKColor(27, 74, 123) },
        new[] { new SKColor(250, 249, 246), new SKColor(238, 235, 228), new SKColor(176, 102, 51) }
    };

    private static readonly SKColor[][] ColorfulPalettes =
    {
        new[] { new SKColor(30, 27, 75), new SKColor(91, 33, 182), new SKColor(244, 63, 94), new SKColor(251, 191, 36) },
        new[] { new SKColor(4, 47, 46), new SKColor(14, 116, 144), new SKColor(249, 115, 22), new SKColor(253, 224, 71) },
        new[] { new SKColor(74, 14, 46), new SKColor(157, 23, 77), new SKColor(252, 211, 77), new SKColor(103, 232, 249) },
        new[] { new SKColor(8, 47, 73), new SKColor(3, 105, 161), new SKColor(251, 146, 60), new SKColor(251, 191, 36) },
        new[] { new SKColor(5, 46, 22), new SKColor(4, 120, 87), new SKColor(253, 224, 71), new SKColor(252, 165, 165) },
        new[] { new SKColor(46, 16, 101), new SKColor(162, 28, 175), new SKColor(34, 211, 238), new SKColor(253, 186, 116) },
        new[] { new SKColor(15, 23, 42), new SKColor(51, 65, 85), new SKColor(244, 114, 182), new SKColor(52, 211, 153) },
        new[] { new SKColor(42, 18, 8), new SKColor(146, 64, 14), new SKColor(253, 230, 138), new SKColor(253, 164, 175) }
    };

    public static SKColor[] Pick(Poster poster, string mode)
    {
        var seed = poster.EventDate.Year * 10000 + poster.EventDate.Month * 100 + poster.EventDate.Day;
        if (!string.IsNullOrWhiteSpace(poster.Category))
        {
            seed += poster.Category.GetHashCode(StringComparison.OrdinalIgnoreCase);
        }

        var source = mode switch
        {
            "light" => LightPalettes,
            "dark" => DarkPalettes,
            _ => ColorfulPalettes
        };

        return source[Math.Abs(seed) % source.Length];
    }
}

/// <summary>Selects an event-themed slogan and quote for the SRV school poster layout.</summary>
public static class SrvCopy
{
    public static (string Slogan, string Quote, string Attribution) For(Poster poster)
    {
        var text = $"{poster.EventTitle} {poster.Category}";

        if (ContainsAny(text, "peace", "war", "bomb", "hiroshima", "nagasaki", "conflict", "violence", "independ", "freedom"))
        {
            return ("REMEMBER THE PAST. PROMISE THE FUTURE.",
                "Peace cannot be kept by force; it can only be achieved by understanding.",
                "Albert Einstein");
        }

        if (ContainsAny(text, "health", "disease", "virus", "epidemic", "medicine", "mental"))
        {
            return ("HEALTH IS THE GREATEST WEALTH.",
                "The first wealth is health.",
                "Ralph Waldo Emerson");
        }

        if (ContainsAny(text, "space", "astronaut", "moon", "rocket", "science", "technology", "invent", "discover"))
        {
            return ("REACH FOR THE STARS.",
                "The Earth is the cradle of humanity, but mankind cannot stay in the cradle forever.",
                "Konstantin Tsiolkovsky");
        }

        if (ContainsAny(text, "education", "school", "teacher", "student", "children", "book", "library"))
        {
            return ("LEARNING TODAY. LEADING TOMORROW.",
                "Education is the most powerful weapon which you can use to change the world.",
                "Nelson Mandela");
        }

        if (ContainsAny(text, "earth", "nature", "environment", "water", "climate", "forest", "animal"))
        {
            return ("PROTECT NATURE. SECURE THE FUTURE.",
                "The earth is what we all have in common.",
                "Wendell Berry");
        }

        if (ContainsAny(text, "birth", "born", "jubilee", "anniversary"))
        {
            return ("CELEBRATE THE LIVES THAT SHAPED US.",
                "The only way to do great work is to love what you do.",
                "Steve Jobs");
        }

        if (ContainsAny(text, "hope", "dream", "right", "human", "equal", "justice", "celebration"))
        {
            return ("A BETTER WORLD BEGINS WITH US.",
                "The future depends on what we do in the present.",
                "Mahatma Gandhi");
        }

        return ("LEARN FROM THE PAST. BUILD THE FUTURE.",
            "Every day is a chance to make a difference.",
            "Anonymous");
    }

    /// <summary>Returns a themed educational paragraph for the poster body.</summary>
    public static string ParagraphFor(Poster poster)
    {
        var sentence = Bucket(poster) switch
        {
            "peace" => " Moments like this remind us that understanding is the foundation of lasting peace, and that dialogue always conquers conflict.",
            "health" => " This day reminds us how science, courage and care together build a healthier, safer world for every community.",
            "space" => " This moment shows how curiosity, teamwork and courage let humanity reach beyond the known and inspire the next generation of explorers.",
            "education" => " This event reminds us that knowledge and discipline are the most powerful forces for shaping a better future.",
            "nature" => " This event reminds us that human life is deeply woven into nature, and that protecting our planet protects our own future.",
            "birth" => " This day honours the power of a single life to inspire generations, and encourages every student to dream boldly.",
            _ => " Every day carries a lesson, and understanding the past helps us build a wiser, kinder and more responsible future."
        };

        return poster.EventTitle + sentence;
    }

    /// <summary>Returns five themed benefit points for the "Benefits & Importance" box.</summary>
    public static string[] BenefitsFor(Poster poster)
    {
        return Bucket(poster) switch
        {
            "peace" => new[]
            {
                "Builds awareness of world peace and harmony",
                "Encourages dialogue over conflict",
                "Honours the memory of the past",
                "Inspires students to become peacemakers",
                "Connects history to today's world"
            },
            "health" => new[]
            {
                "Creates awareness of healthy living",
                "Teaches the value of hygiene and care",
                "Builds empathy for the affected and vulnerable",
                "Encourages a scientific understanding of disease",
                "Promotes responsibility for community wellbeing"
            },
            "space" => new[]
            {
                "Sparks curiosity about the universe",
                "Encourages scientific and logical thinking",
                "Inspires careers in science and technology",
                "Shows the power of teamwork in exploration",
                "Connects classroom lessons to real missions"
            },
            "education" => new[]
            {
                "Highlights the lifelong value of learning",
                "Celebrates teachers and mentors",
                "Encourages curiosity and questioning",
                "Builds discipline, focus and character",
                "Shows how education transforms lives"
            },
            "nature" => new[]
            {
                "Builds love and respect for the environment",
                "Teaches conservation of precious resources",
                "Encourages responsible citizenship",
                "Protects biodiversity for future generations",
                "Connects science to everyday life"
            },
            "birth" => new[]
            {
                "Celebrates lives that shaped history",
                "Draws inspiration from great achievers",
                "Connects the past with the present",
                "Encourages students to dream big",
                "Honours lasting contributions to society"
            },
            _ => new[]
            {
                "Builds positive and forward-looking thinking",
                "Encourages responsibility and action",
                "Celebrates human dignity and achievement",
                "Inspires service to others and the community",
                "Connects our values to everyday life"
            }
        };
    }

    /// <summary>Returns a themed "Did You Know?" fact for the event.</summary>
    public static string FactFor(Poster poster)
    {
        var year = poster.Events.FirstOrDefault(e => e.Year.HasValue)?.Year ?? poster.EventDate.Year;
        return Bucket(poster) switch
        {
            "peace" => $"On {year}, this event became a turning point that taught the world that understanding is the first step to lasting peace.",
            "health" => $"This event in {year} changed how the whole world responds to public health emergencies and protects communities.",
            "space" => $"This milestone in {year} took years of teamwork, science and courage to achieve — and it still inspires young minds today.",
            "education" => $"Moments like this one in {year} show how knowledge, courage and discipline can change the course of history.",
            "nature" => $"This event in {year} is a powerful reminder of how deeply human life is connected to the natural world.",
            "birth" => $"People born in {year} grew up to shape the world we live in today — great achievements often begin as simple dreams.",
            _ => $"This day in {year} reminds us that every day holds the power to change the future for the better."
        };
    }

    private static string Bucket(Poster poster)
    {
        var text = $"{poster.EventTitle} {poster.Category}";
        if (ContainsAny(text, "peace", "war", "bomb", "hiroshima", "nagasaki", "conflict", "violence", "independ", "freedom"))
        {
            return "peace";
        }

        if (ContainsAny(text, "health", "disease", "virus", "epidemic", "medicine", "mental"))
        {
            return "health";
        }

        if (ContainsAny(text, "space", "astronaut", "moon", "rocket", "science", "technology", "invent", "discover"))
        {
            return "space";
        }

        if (ContainsAny(text, "education", "school", "teacher", "student", "children", "book", "library"))
        {
            return "education";
        }

        if (ContainsAny(text, "earth", "nature", "environment", "water", "climate", "forest", "animal", "mudslide", "volcano", "flood", "earthquake"))
        {
            return "nature";
        }

        if (ContainsAny(text, "birth", "born", "jubilee", "anniversary"))
        {
            return "birth";
        }

        return "default";
    }

    private static bool ContainsAny(string text, params string[] keys)
    {
        return keys.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
