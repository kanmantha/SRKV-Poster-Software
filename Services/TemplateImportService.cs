using System.Text.Json;
using DailyPosterGenerator.Models;
using Microsoft.AspNetCore.Http;
using SkiaSharp;

namespace DailyPosterGenerator.Services;

public interface ITemplateImportService
{
    Task<TemplateImportResult> ImportAsync(
        int tenantId,
        string name,
        string? description,
        string sector,
        IFormFile file,
        IReadOnlyList<ImportBox>? boxes = null,
        PosterTreatmentRequest? treatment = null,
        CancellationToken ct = default);

    /// <summary>Heuristically locates likely text blocks and logo areas in an uploaded poster so
    /// the import editor can offer one-click erasing. Text detection skips colourful
    /// regions (logos) and logo detection skips low-saturation text.</summary>
    Task<DetectionResult> DetectRegionsAsync(IFormFile file, CancellationToken ct = default);

    /// <summary>Same detection as <see cref="DetectRegionsAsync"/> but on a stored image under wwwroot.</summary>
    Task<DetectionResult> DetectRegionsFromFileAsync(string? relativePath, CancellationToken ct = default);

    /// <summary>
    /// Re-applies erase / erase-logo / keep boxes to a template's untouched original
    /// upload and replaces its processed background, text regions and box history.
    /// </summary>
    Task<TemplateImportResult> ReprocessAsync(PosterTemplate template, IReadOnlyList<ImportBox>? boxes, CancellationToken ct = default);

    /// <summary>
    /// Chat-style update: reads a plain-language instruction ("remove all text and
    /// logos, make it blue"), figures out what to erase and which colour refresh to
    /// apply, renders a small preview of the result and reports what it understood.
    /// </summary>
    Task<AiUpdateResult> ApplyInstructionAsync(IFormFile? file, string? instruction, CancellationToken ct = default);

    /// <summary>
    /// Same chat-style update but applied to an existing imported template's stored poster,
    /// persisting the new background, boxes and colour treatment on the template itself.
    /// </summary>
    Task<AiUpdateResult> ApplyInstructionToTemplateAsync(PosterTemplate template, string? instruction, CancellationToken ct = default);

    /// <summary>
    /// Renders a small colour-treatment preview (no erasing) of an uploaded poster so
    /// the import wizard can show what each refresh option looks like before saving.
    /// </summary>
    Task<byte[]?> RenderTreatmentPreviewAsync(IFormFile? file, PosterTreatmentRequest? treatment, CancellationToken ct = default);
}

public record TemplateImportResult(bool Success, PosterTemplate? Template, string? Error);

/// <summary>Auto-detected regions of an uploaded poster, normalized 0..1.</summary>
public record DetectionResult(IReadOnlyList<ImportBox> TextBoxes, IReadOnlyList<ImportBox> LogoBoxes);

/// <summary>
/// Colour refresh chosen in the import wizard: keep the original look, wash it with one
/// brand colour, boost faded colours, convert to black &amp; white, or discard the old art
/// for a fresh themed gradient background.
/// </summary>
public record PosterTreatmentRequest(
    string Kind,
    string? TintHex = null,
    float TintStrength = 0.4f,
    string? FreshTheme = null,
    string? FreshAccent = null);

/// <summary>Parsed outcome of one chat instruction.</summary>
public sealed record ParsedInstruction(bool RemoveText, bool RemoveLogos, PosterTreatmentRequest Treatment);

/// <summary>Result of applying one chat instruction to an uploaded poster.</summary>
public sealed record AiUpdateResult(
    bool Success,
    byte[]? Image,
    string Summary,
    IReadOnlyList<ImportBox> Boxes,
    PosterTreatmentRequest Treatment);

/// <summary>
/// Turns an uploaded poster image into a reusable PosterTemplate: the image becomes
/// the background layout and the dominant colours are extracted so daily text can be
/// rendered on top with the same look and feel. The user draws "erase" boxes over the
/// old text (which is removed by re-blending the surrounding background) and "keep"
/// boxes over logos (which stay untouched).
/// </summary>
public class TemplateImportService : ITemplateImportService
{
    private const int MaxFileBytes = 10 * 1024 * 1024;
    private static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".webp" };

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<TemplateImportService> _logger;

    public TemplateImportService(IWebHostEnvironment env, ILogger<TemplateImportService> logger)
    {
        _env = env;
        _logger = logger;
    }

    public async Task<TemplateImportResult> ImportAsync(
        int tenantId,
        string name,
        string? description,
        string sector,
        IFormFile file,
        IReadOnlyList<ImportBox>? boxes = null,
        PosterTreatmentRequest? treatment = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new TemplateImportResult(false, null, "Give the template a name.");
        }

        if (file is null || file.Length == 0)
        {
            return new TemplateImportResult(false, null, "Choose a poster image to upload.");
        }

        if (file.Length > MaxFileBytes)
        {
            return new TemplateImportResult(false, null, "Image must be 10 MB or smaller.");
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            return new TemplateImportResult(false, null, "Only PNG, JPG, JPEG and WEBP images are supported.");
        }

        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        SKImage? image = null;
        try
        {
            image = SKImage.FromEncodedData(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode uploaded poster for template import.");
            return new TemplateImportResult(false, null, "That image could not be read. Try a PNG or JPG.");
        }

        using (image)
        {
            SKImage layout;
            var validBoxes = NormalizeBoxes(boxes);
            try
            {
                layout = validBoxes.Count > 0
                    ? ApplyBoxEdits(image, validBoxes)
                    : image;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Box editing failed; importing the image as-is.");
                layout = image;
            }

            using (layout)
            {
                var refreshed = NormalizeTreatment(treatment);
                SKImage finalLayout;
                try
                {
                    finalLayout = ApplyTreatment(layout, refreshed);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Colour treatment failed; importing with the original look.");
                    finalLayout = layout;
                }

                using (finalLayout)
                {
                    var (accent, textColor) = AnalyzeColors(finalLayout);
                    if (refreshed.Kind == "fresh")
                    {
                        if (HexColor.TryParse(refreshed.FreshAccent, out var chosen))
                        {
                            accent = HexColor.ToHex(chosen);
                        }

                        textColor = HexColor.ToHex(HexColor.AutoTextColor(
                            AverageColor(finalLayout)));
                    }

                    var savedPath = await SaveBackgroundAsync(tenantId, finalLayout, ext, ct);
                    var originalPath = await SaveOriginalAsync(tenantId, bytes, ext, ct);

                    var template = new PosterTemplate
                    {
                        TenantId = tenantId,
                        Name = name.Trim(),
                        Description = description?.Trim(),
                        Sector = SectorCatalog.Normalize(sector),
                        IsSystem = false,
                        IsImported = true,
                        IsActive = true,
                        Theme = "template",
                        AccentColor = accent,
                        TextColor = textColor,
                        BackgroundImagePath = savedPath,
                        OriginalBackgroundPath = originalPath,
                        ImportBoxesJson = validBoxes.Count > 0 ? JsonSerializer.Serialize(validBoxes) : null,
                        TreatmentJson = refreshed.Kind == "original" ? null : JsonSerializer.Serialize(refreshed),
                        BackgroundDim = 30,
                        TextRegionsJson = BuildRegionsJson(validBoxes),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    return new TemplateImportResult(true, template, null);
                }
            }
        }
    }

    public async Task<DetectionResult> DetectRegionsAsync(IFormFile file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return new DetectionResult(Array.Empty<ImportBox>(), Array.Empty<ImportBox>());
        }

        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        try
        {
            using var image = SKImage.FromEncodedData(bytes);
            return DetectCore(image);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto detection failed.");
            return new DetectionResult(Array.Empty<ImportBox>(), Array.Empty<ImportBox>());
        }
    }

    public Task<DetectionResult> DetectRegionsFromFileAsync(string? relativePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Task.FromResult(new DetectionResult(Array.Empty<ImportBox>(), Array.Empty<ImportBox>()));
        }

        var webRoot = _env.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return Task.FromResult(new DetectionResult(Array.Empty<ImportBox>(), Array.Empty<ImportBox>()));
        }

        var fullPath = Path.Combine(webRoot, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return Task.FromResult(new DetectionResult(Array.Empty<ImportBox>(), Array.Empty<ImportBox>()));
        }

        try
        {
            using var image = SKImage.FromEncodedData(fullPath);
            return Task.FromResult(DetectCore(image));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto detection failed for {Path}.", relativePath);
            return Task.FromResult(new DetectionResult(Array.Empty<ImportBox>(), Array.Empty<ImportBox>()));
        }
    }

    private static DetectionResult DetectCore(SKImage image)
    {
        var logos = DetectLogoBlocks(image);
        var text = DetectTextBlocks(image, logos);
        return new DetectionResult(text, logos);
    }

    public async Task<TemplateImportResult> ReprocessAsync(
        PosterTemplate template, IReadOnlyList<ImportBox>? boxes, CancellationToken ct = default)
    {
        // Templates imported before re-editing existed have no stored original; fall
        // back to their processed background and promote it to the new "original".
        var sourceRelative = string.IsNullOrWhiteSpace(template?.OriginalBackgroundPath)
            ? template?.BackgroundImagePath
            : template.OriginalBackgroundPath;
        if (template is null || string.IsNullOrWhiteSpace(sourceRelative))
        {
            return new TemplateImportResult(false, null, "This template has no poster image to re-edit.");
        }

        var webRoot = _env.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return new TemplateImportResult(false, null, "Storage is not available.");
        }

        var fullPath = Path.Combine(
            webRoot, sourceRelative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return new TemplateImportResult(false, null, "The poster image file is missing on disk.");
        }

        SKImage? image;
        try
        {
            image = SKImage.FromEncodedData(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode the original upload of template {TemplateId}.", template.Id);
            return new TemplateImportResult(false, null, "The original image could not be read.");
        }

        using (image)
        {
            var validBoxes = NormalizeBoxes(boxes);
            SKImage layout;
            try
            {
                layout = validBoxes.Count > 0 ? ApplyBoxEdits(image, validBoxes) : image;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reprocessing box editing failed for template {TemplateId}; using the raw original.", template.Id);
                layout = image;
            }

            // The colour refresh chosen at import (or via a later instruction) is baked
            // into the background, so re-apply it after every re-edit from the raw original.
            var storedTreatment = DeserializeTreatment(template.TreatmentJson);
            if (storedTreatment is not null && storedTreatment.Kind != "original")
            {
                try
                {
                    var treated = ApplyTreatment(layout, storedTreatment);
                    if (!ReferenceEquals(treated, layout))
                    {
                        // Dispose the superseded image explicitly; assigning inside a using
                        // statement would dispose the original anyway (CS0728).
                        var superseded = layout;
                        layout = treated;
                        superseded.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Re-applying the stored colour treatment failed for template {TemplateId}.", template.Id);
                }
            }

            using (layout)
            {
                var savedPath = await SaveBackgroundAsync(template.TenantId, layout, ".png", ct);
                if (savedPath is null)
                {
                    return new TemplateImportResult(false, null, "Could not save the updated background.");
                }

                var previousBg = template.BackgroundImagePath;
                if (string.IsNullOrWhiteSpace(template.OriginalBackgroundPath))
                {
                    // Legacy template: the pre-edit image becomes the new editing source.
                    template.OriginalBackgroundPath = sourceRelative;
                }

                // Never delete the file that just became (or already was) the original.
                if (!string.Equals(previousBg, template.OriginalBackgroundPath, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteImportedFile(previousBg, savedPath);
                }

                // Accent/text colours are left untouched so user customizations survive.
                template.BackgroundImagePath = savedPath;
                template.TextRegionsJson = BuildRegionsJson(validBoxes);
                template.ImportBoxesJson = validBoxes.Count > 0 ? JsonSerializer.Serialize(validBoxes) : null;
                template.UpdatedAt = DateTime.UtcNow;
                return new TemplateImportResult(true, template, null);
            }
        }
    }

    /// <summary>
    /// Chat-style update: reads a plain-language instruction ("remove all text and
    /// logos, make it blue"), figures out what to erase and which colour refresh to
    /// apply, renders a small preview of the result and reports what it understood.
    /// </summary>
    public async Task<AiUpdateResult> ApplyInstructionAsync(
        IFormFile? file, string? instruction, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0 || file.Length > MaxFileBytes)
        {
            return new AiUpdateResult(false, null, "Choose a poster image first.", Array.Empty<ImportBox>(), new PosterTreatmentRequest("original"));
        }

        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        SKImage? image;
        try
        {
            image = SKImage.FromEncodedData(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode uploaded poster for instruction preview.");
            return new AiUpdateResult(false, null, "That image could not be read.", Array.Empty<ImportBox>(), new PosterTreatmentRequest("original"));
        }

        using (image)
        {
            var parsed = ParseInstruction(instruction ?? string.Empty);

            var working = image;
            var boxes = new List<ImportBox>();
            if (parsed.RemoveText)
            {
                boxes.AddRange(DetectTextBlocks(image, parsed.RemoveLogos ? DetectLogoBlocks(image) : new List<ImportBox>()));
            }

            if (parsed.RemoveLogos)
            {
                boxes.AddRange(DetectLogoBlocks(image));
            }

            var validBoxes = NormalizeBoxes(boxes);
            try
            {
                if (validBoxes.Count > 0)
                {
                    var edited = ApplyBoxEdits(image, validBoxes);
                    if (!ReferenceEquals(edited, image))
                    {
                        working = edited;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Instruction erasing failed; previewing without erasures.");
                working = image;
                validBoxes = new List<ImportBox>();
            }

            using (working)
            using (var treated = ApplyTreatment(working, parsed.Treatment))
            {
                const int previewWidth = 560;
                var scale = Math.Min(1f, previewWidth / (float)Math.Max(1, treated.Width));
                var info = new SKImageInfo(
                    (int)(treated.Width * scale), (int)(treated.Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
                using var small = SKSurface.Create(info)
                    ?? throw new InvalidOperationException($"Treatment preview surface failed ({info.Width}x{info.Height}).");
                small.Canvas.DrawImage(treated, new SKRect(0, 0, info.Width, info.Height), SKSamplingOptions.Default, null);
                using var snapshot = small.Snapshot()
                    ?? throw new InvalidOperationException("Treatment preview snapshot failed.");
                using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 82);

                return new AiUpdateResult(
                    true,
                    data?.ToArray(),
                    DescribeInstruction(parsed, validBoxes),
                    validBoxes,
                    parsed.Treatment);
            }
        }
    }

    /// <summary>Parsed outcome of one chat instruction.</summary>
    public sealed record ParsedInstruction(bool RemoveText, bool RemoveLogos, PosterTreatmentRequest Treatment);

    private static readonly Dictionary<string, string> InstructionColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = "#E53935",
        ["maroon"] = "#8E0000",
        ["pink"] = "#EC407A",
        ["orange"] = "#FB8C00",
        ["gold"] = "#FFB300",
        ["golden"] = "#FFB300",
        ["yellow"] = "#FDD835",
        ["green"] = "#43A047",
        ["teal"] = "#00897B",
        ["cyan"] = "#00ACC1",
        ["blue"] = "#1E88E5",
        ["navy"] = "#1565C0",
        ["purple"] = "#8E24AA",
        ["violet"] = "#7E57C2",
        ["magenta"] = "#D81B60",
        ["brown"] = "#6D4C41"
    };

    /// <summary>Turns free text into erase actions plus a colour treatment. Keyword
    /// based on purpose: it runs offline and always explains what it understood.</summary>
    public static ParsedInstruction ParseInstruction(string instruction)
    {
        var text = (instruction ?? string.Empty).ToLowerInvariant();

        static bool HasVerb(string t) =>
            System.Text.RegularExpressions.Regex.IsMatch(t, @"\b(remove|removing|delete|clear|erase|clean|strip|drop)\b");
        var wantsText = HasVerb(text)
            && System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(texts?|matter|words?|writing|letters?|numbers?|headings?|captions?)\b");
        var wantsLogos = HasVerb(text)
            && System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(logos?|emblems?|seals?|symbols?|stamps?|watermarks?|monograms?)\b");

        var treatment = new PosterTreatmentRequest("original");

        var grayscale = System.Text.RegularExpressions.Regex.IsMatch(
            text, @"\b(black\s*(and|&|n)?\s*white|b\s*&\s*w|bw|gr[ae]y\s*-?\s*scale|monochrome|colourless|colorless|no colou?rs?)\b");
        var freshBg = System.Text.RegularExpressions.Regex.IsMatch(
            text, @"\b((new|changed?|replace[d]?|different|fresh|gradient)[\s-]*(background|bg)|background[\s-]*(change|replace))\b");
        var enhance = System.Text.RegularExpressions.Regex.IsMatch(
            text, @"\b(enhance|enhanced|brighten|brighter|vivid|sharpen|sharper|\bhd\b|pop|boost|refresh(?!ed)?\s+colou?rs?)\b")
            && !freshBg;

        // A named colour drives the request: "<colour> background" swaps the whole
        // backdrop, otherwise it becomes a tint wash over the original art.
        foreach (var kv in InstructionColors)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{kv.Key}\b"))
            {
                continue;
            }

            var colourBg = System.Text.RegularExpressions.Regex.IsMatch(text, $@"{kv.Key}[\s-]*(background|bg|theme)")
                || (freshBg && !System.Text.RegularExpressions.Regex.IsMatch(text, @"colou?rful"));
            if (colourBg)
            {
                HexColor.TryParse(kv.Value, out var c);
                var theme = HexColor.Luminance(c) < 90 ? "dark" : "light";
                treatment = new PosterTreatmentRequest("fresh", FreshTheme: theme, FreshAccent: kv.Value);
            }
            else if (!grayscale)
            {
                treatment = new PosterTreatmentRequest("tint", TintHex: kv.Value, TintStrength: 0.45f);
            }

            break;
        }

        if (grayscale)
        {
            treatment = new PosterTreatmentRequest("grayscale");
        }
        else if (freshBg && treatment.Kind != "fresh")
        {
            treatment = System.Text.RegularExpressions.Regex.IsMatch(text, @"\bdark\b")
                ? new PosterTreatmentRequest("fresh", FreshTheme: "dark")
                : System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(white|light)\b")
                    ? new PosterTreatmentRequest("fresh", FreshTheme: "light")
                    : new PosterTreatmentRequest("fresh", FreshTheme: "colorful");
        }
        else if (enhance && treatment.Kind == "original")
        {
            treatment = new PosterTreatmentRequest("enhance");
        }

        return new ParsedInstruction(wantsText, wantsLogos, treatment);
    }

    private static string DescribeInstruction(ParsedInstruction parsed, IReadOnlyList<ImportBox> boxes)
    {
        if (!parsed.RemoveText && !parsed.RemoveLogos && parsed.Treatment.Kind == "original")
        {
            return "I couldn't find a specific change in that. Mention what to remove (text or logos) or a look, e.g. \"remove all text\", \"make it blue\" or \"black and white\".";
        }

        var parts = new List<string>();
        if (parsed.RemoveText)
        {
            parts.Add($"erasing {boxes.Count(b => b.Type == "erase")} text area{(boxes.Count(b => b.Type == "erase") == 1 ? "" : "s")}");
        }

        if (parsed.RemoveLogos)
        {
            parts.Add($"removing {boxes.Count(b => b.Type == "erase-logo")} logo area{(boxes.Count(b => b.Type == "erase-logo") == 1 ? "" : "s")}");
        }

        switch (parsed.Treatment.Kind)
        {
            case "tint":
                parts.Add($"{(parsed.Treatment.TintHex ?? "#000000").TrimStart('#')} colour wash");
                break;
            case "enhance":
                parts.Add("colours enhanced");
                break;
            case "grayscale":
                parts.Add("converted to black & white");
                break;
            case "fresh":
                parts.Add($"fresh {(parsed.Treatment.FreshTheme ?? "colorful")} background");
                break;
        }

        return char.ToUpperInvariant(parts[0][0]) + parts[0][1..]
            + (parts.Count > 1 ? ", " + string.Join(", ", parts.Skip(1)) : string.Empty) + ".";
    }

    // ---------------------------------------------------------- colour treatments

    /// <summary>Fills in defaults and clamps values so a malformed request can never
    /// produce an invalid treatment.</summary>
    public static PosterTreatmentRequest NormalizeTreatment(PosterTreatmentRequest? treatment)
    {
        if (treatment is null || string.IsNullOrWhiteSpace(treatment.Kind))
        {
            return new PosterTreatmentRequest("original");
        }

        var kind = treatment.Kind.Trim().ToLowerInvariant();
        if (kind is not ("tint" or "enhance" or "grayscale" or "fresh"))
        {
            return new PosterTreatmentRequest("original");
        }

        var strength = Math.Clamp(treatment.TintStrength <= 0 ? 0.4f : treatment.TintStrength, 0.05f, 0.85f);
        var theme = (treatment.FreshTheme ?? "colorful").Trim().ToLowerInvariant();
        if (theme is not ("colorful" or "light" or "dark"))
        {
            theme = "colorful";
        }

        return new PosterTreatmentRequest(kind, treatment.TintHex, strength, theme, treatment.FreshAccent);
    }

    public async Task<byte[]?> RenderTreatmentPreviewAsync(
        IFormFile? file, PosterTreatmentRequest? treatment, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0 || file.Length > MaxFileBytes)
        {
            return null;
        }

        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        try
        {
            using var image = SKImage.FromEncodedData(bytes);
            const int previewWidth = 560;
            var scale = Math.Min(1f, previewWidth / (float)image.Width);
            var info = new SKImageInfo(
                (int)(image.Width * scale), (int)(image.Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
            using var small = SKSurface.Create(info);
            small.Canvas.DrawImage(image, new SKRect(0, 0, info.Width, info.Height), SKSamplingOptions.Default, null);
            using var treated = ApplyTreatment(small.Snapshot(), NormalizeTreatment(treatment));
            using var data = treated.Encode(SKEncodedImageFormat.Jpeg, 82);
            return data?.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Treatment preview failed.");
            return null;
        }
    }

    private static SKImage ApplyTreatment(SKImage image, PosterTreatmentRequest t)
    {
        switch (t.Kind)
        {
            case "grayscale":
                return ProcessPixels(image, (r, g, b) =>
                {
                    var l = (byte)Math.Clamp(0.299f * r + 0.587f * g + 0.114f * b, 0, 255);
                    return (l, l, l);
                });
            case "enhance":
                return ProcessPixels(image, (r, g, b) =>
                {
                    var avg = (r + g + b) / 3f;
                    byte Map(float v)
                    {
                        v = (v - 128f) * 1.16f + 128f;      // contrast
                        v = avg + (v - avg) * 1.3f;         // saturation
                        v = v * 1.06f + 6f;                 // brightness
                        return (byte)Math.Clamp(v, 0, 255);
                    }
                    return (Map(r), Map(g), Map(b));
                });
            case "tint":
                if (!HexColor.TryParse(t.TintHex, out var tint))
                {
                    return image;
                }

                var s = t.TintStrength;
                return ProcessPixels(image, (r, g, b) =>
                {
                    var lum = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
                    var shade = 0.35f + 0.75f * lum;        // keep shadows dark, highlights tinted
                    byte Mix(byte orig, byte tc)
                    {
                        var target = Math.Clamp(tc * shade * 1.12f, 0, 255);
                        return (byte)Math.Clamp(orig * (1 - s) + target * s, 0, 255);
                    }
                    return (Mix(r, tint.Red), Mix(g, tint.Green), Mix(b, tint.Blue));
                });
            case "fresh":
                return FreshBackground(image.Width, image.Height, t.FreshTheme ?? "colorful", t.FreshAccent);
            default:
                return image;
        }
    }

    /// <summary>Reads back a persisted treatment; null/invalid means "original colours".</summary>
    public static PosterTreatmentRequest? DeserializeTreatment(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return NormalizeTreatment(JsonSerializer.Deserialize<PosterTreatmentRequest>(json));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Chat-style update for an already-imported template: runs the instruction against
    /// its stored original poster, bakes erasures plus any colour refresh into a new
    /// background and updates the template in place. The untouched upload stays safe.
    /// </summary>
    /// <summary>
    /// Maps a chat instruction onto a procedurally generated (Studio) template:
    /// colour words retune the accent, style words switch light/dark/colorful.
    /// </summary>
    private static AiUpdateResult ApplyInstructionToStudioTemplate(PosterTemplate template, string? instruction)
    {
        var parsed = ParseInstruction(instruction ?? string.Empty);
        var t = parsed.Treatment;
        var changed = false;
        var notes = new List<string>();

        switch (t.Kind)
        {
            case "fresh":
                template.Theme = t.FreshTheme switch { "dark" => "dark", "light" => "light", _ => "colorful" };
                changed = true;
                notes.Add($"style set to {template.Theme}");
                if (!string.IsNullOrWhiteSpace(t.FreshAccent))
                {
                    template.AccentColor = t.FreshAccent;
                    notes.Add($"accent {t.FreshAccent.ToUpperInvariant()}");
                }
                break;

            case "tint":
                template.AccentColor = t.TintHex;
                if (t.TintHex is not null && HexColor.TryParse(t.TintHex, out var tint) && HexColor.Luminance(tint) > 180f)
                {
                    template.Theme = "light";
                }
                changed = true;
                notes.Add($"accent colour set to {t.TintHex?.ToUpperInvariant()}");
                break;

            case "grayscale":
                template.AccentColor = "#6B7280";
                template.Theme = "light";
                changed = true;
                notes.Add("a neutral monochrome accent was applied");
                break;

            case "enhance":
                notes.Add("studio templates are drawn fresh every day, so they are always full quality");
                break;

            default:
                if (parsed.RemoveText || parsed.RemoveLogos)
                {
                    notes.Add("this template draws its text and icons fresh each time, so there is nothing baked in to erase");
                }
                else
                {
                    var lower = (instruction ?? string.Empty).ToLowerInvariant();
                    if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b(light|bright|airy|soft|pastel)\b"))
                    {
                        template.Theme = "light";
                        changed = true;
                        notes.Add("style set to light");
                    }
                    else if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b(dark|moody|midnight|night)\b"))
                    {
                        template.Theme = "dark";
                        changed = true;
                        notes.Add("style set to dark");
                    }
                    else if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b(colou?rful|vibrant|festive|bold)\b"))
                    {
                        template.Theme = "colorful";
                        changed = true;
                        notes.Add("style set to colourful");
                    }
                }
                break;
        }

        if (changed)
        {
            template.UpdatedAt = DateTime.UtcNow;
            return new AiUpdateResult(true, null,
                "Done - " + string.Join(", ", notes) + ". The next poster you generate uses the new look.",
                Array.Empty<ImportBox>(), new PosterTreatmentRequest("original"));
        }

        return new AiUpdateResult(true, null,
            notes.Count > 0
                ? char.ToUpperInvariant(notes[0][0]) + notes[0][1..] + "."
                : "Describe a colour or style instead, e.g. \"make it dark blue\", \"black and white\" or \"make it light and airy\".",
            Array.Empty<ImportBox>(), new PosterTreatmentRequest("original"));
    }

    public async Task<AiUpdateResult> ApplyInstructionToTemplateAsync(
        PosterTemplate template, string? instruction, CancellationToken ct = default)
    {
        if (template is null)
        {
            return new AiUpdateResult(false, null, "This template has no poster image to update.", Array.Empty<ImportBox>(), new PosterTreatmentRequest("original"));
        }

        // Studio templates are generated procedurally (theme + accent), so instructions
        // retune those settings instead of editing a stored image.
        if (!template.IsImported)
        {
            return ApplyInstructionToStudioTemplate(template, instruction);
        }

        var sourceRelative = string.IsNullOrWhiteSpace(template.OriginalBackgroundPath)
            ? template.BackgroundImagePath
            : template.OriginalBackgroundPath;
        if (string.IsNullOrWhiteSpace(sourceRelative))
        {
            return new AiUpdateResult(false, null, "This template has no poster image to update.", Array.Empty<ImportBox>(), new PosterTreatmentRequest("original"));
        }

        var webRoot = _env.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return new AiUpdateResult(false, null, "Storage is not available.", Array.Empty<ImportBox>(), new PosterTreatmentRequest("original"));
        }

        var fullPath = Path.Combine(webRoot, sourceRelative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return new AiUpdateResult(false, null, "The poster image file is missing on disk.", Array.Empty<ImportBox>(), new PosterTreatmentRequest("original"));
        }

        SKImage? image;
        try
        {
            image = SKImage.FromEncodedData(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode stored poster of template {TemplateId}.", template.Id);
            return new AiUpdateResult(false, null, "The poster image could not be read.", Array.Empty<ImportBox>(), new PosterTreatmentRequest("original"));
        }

        using (image)
        {
            var parsed = ParseInstruction(instruction ?? string.Empty);

            // A pure erase instruction keeps whatever colour refresh the poster already has.
            var effective = parsed.Treatment.Kind == "original"
                ? (DeserializeTreatment(template.TreatmentJson) ?? parsed.Treatment)
                : parsed.Treatment;

            var boxes = new List<ImportBox>();
            if (parsed.RemoveText)
            {
                boxes.AddRange(DetectTextBlocks(image, parsed.RemoveLogos ? DetectLogoBlocks(image) : new List<ImportBox>()));
            }

            if (parsed.RemoveLogos)
            {
                boxes.AddRange(DetectLogoBlocks(image));
            }

            var validBoxes = NormalizeBoxes(boxes);

            SKImage working = image;
            try
            {
                if (validBoxes.Count > 0)
                {
                    var edited = ApplyBoxEdits(image, validBoxes);
                    if (!ReferenceEquals(edited, image))
                    {
                        working = edited;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Instruction erasing failed for template {TemplateId}.", template.Id);
                working = image;
                validBoxes = new List<ImportBox>();
            }

            using (working)
            using (var final = ApplyTreatment(working, effective))
            {
                var savedPath = await SaveBackgroundAsync(template.TenantId, final, ".png", ct);
                if (savedPath is null)
                {
                    return new AiUpdateResult(false, null, "Could not save the updated poster.", Array.Empty<ImportBox>(), effective);
                }

                var previousBg = template.BackgroundImagePath;
                if (!string.Equals(previousBg, template.OriginalBackgroundPath, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteImportedFile(previousBg, savedPath);
                }

                template.BackgroundImagePath = savedPath;
                template.TreatmentJson = effective.Kind == "original" ? null : JsonSerializer.Serialize(effective);

                // When this instruction changed the look, re-pick readable text/accent
                // colours from the new artwork. Pure erase instructions keep any colours
                // the user may have customised.
                if (parsed.Treatment.Kind != "original")
                {
                    var (newAccent, newText) = AnalyzeColors(final);
                    if (effective.Kind == "fresh" && HexColor.TryParse(effective.FreshAccent, out var chosenAccent))
                    {
                        newAccent = HexColor.ToHex(chosenAccent);
                    }

                    template.AccentColor = newAccent;
                    template.TextColor = newText;
                }

                template.TextRegionsJson = BuildRegionsJson(validBoxes);
                template.ImportBoxesJson = validBoxes.Count > 0 ? JsonSerializer.Serialize(validBoxes) : null;
                template.UpdatedAt = DateTime.UtcNow;

                const int previewWidth = 560;
                var scale = Math.Min(1f, previewWidth / (float)Math.Max(1, final.Width));
                var info = new SKImageInfo(
                    (int)(final.Width * scale), (int)(final.Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
                using var small = SKSurface.Create(info);
                byte[]? previewBytes = null;
                if (small is not null)
                {
                    small.Canvas.DrawImage(final, new SKRect(0, 0, info.Width, info.Height), SKSamplingOptions.Default, null);
                    using var snapshot = small.Snapshot();
                    using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 82);
                    previewBytes = data?.ToArray();
                }
                else
                {
                    _logger.LogWarning("Instruction preview surface unavailable for template {TemplateId}.", template.Id);
                }

                return new AiUpdateResult(true, previewBytes, DescribeInstruction(parsed, validBoxes), validBoxes, effective);
            }
        }
    }

    private delegate (byte R, byte G, byte B) PixelMap(byte r, byte g, byte b);

    private static SKImage ProcessPixels(SKImage image, PixelMap map)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Failed to allocate the treatment surface.");
        surface.Canvas.DrawImage(image, 0f, 0f, SKSamplingOptions.Default);
        using var pm = surface.PeekPixels() ?? throw new InvalidOperationException("Failed to read the treatment pixels.");
        var span = pm.GetPixelSpan();
        for (var i = 0; i + 3 < span.Length; i += 4)
        {
            (span[i], span[i + 1], span[i + 2]) = map(span[i], span[i + 1], span[i + 2]);
        }

        return surface.Snapshot();
    }

    /// <summary>Studio-style gradient poster background: a diagonal two-colour wash
    /// plus two soft accent glows, sized to the original upload.</summary>
    private static SKImage FreshBackground(int width, int height, string theme, string? accentHex)
    {
        var (c0, c1, defaultAccent) = theme switch
        {
            "light" => (new SKColor(255, 247, 232), new SKColor(255, 255, 255), new SKColor(255, 111, 0)),
            "dark" => (new SKColor(13, 20, 36), new SKColor(32, 44, 66), new SKColor(66, 165, 245)),
            _ => (new SKColor(233, 30, 99), new SKColor(255, 111, 0), new SKColor(255, 213, 79))
        };

        if (!HexColor.TryParse(accentHex, out var glow))
        {
            glow = defaultAccent;
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Failed to allocate the fresh background surface.");
        var canvas = surface.Canvas;

        using (var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(width, height),
            new[] { c0, c1 }, null, SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(0, 0, width, height, paint);
        }

        DrawGlow(canvas, width * 0.14f, height * 0.10f, Math.Min(width, height) * 0.55f, glow, 0.30f);
        DrawGlow(canvas, width * 0.90f, height * 0.88f, Math.Min(width, height) * 0.62f, glow, 0.18f);

        return surface.Snapshot();
    }

    private static void DrawGlow(SKCanvas canvas, float cx, float cy, float radius, SKColor color, float opacity)
    {
        var alpha = (byte)Math.Clamp(255 * opacity, 0, 255);
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(cx, cy), radius,
            new[] { color.WithAlpha(alpha), color.WithAlpha(0) },
            null, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, canvas.DeviceClipBounds.Width, canvas.DeviceClipBounds.Height, paint);
    }

    private static SKColor AverageColor(SKImage image)
    {
        const int size = 16;
        using var small = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        small.Canvas.DrawImage(image, new SKRect(0, 0, size, size), SKSamplingOptions.Default, null);
        using var pm = small.PeekPixels()!;
        var span = pm.GetPixelSpan();
        long r = 0, g = 0, b = 0;
        var n = 0;
        for (var i = 0; i + 3 < span.Length; i += 4)
        {
            r += span[i];
            g += span[i + 1];
            b += span[i + 2];
            n++;
        }

        return new SKColor((byte)(r / Math.Max(1, n)), (byte)(g / Math.Max(1, n)), (byte)(b / Math.Max(1, n)));
    }

    // --------------------------------------------------------------- box editing

    private static List<ImportBox> NormalizeBoxes(IReadOnlyList<ImportBox>? boxes)
    {
        if (boxes is null)
        {
            return new List<ImportBox>();
        }

        var result = new List<ImportBox>();
        foreach (var b in boxes)
        {
            var x = Math.Clamp(b.X, 0f, 0.99f);
            var y = Math.Clamp(b.Y, 0f, 0.99f);
            var w = Math.Clamp(b.W, 0.02f, 1f - x);
            var h = Math.Clamp(b.H, 0.02f, 1f - y);
            var type = b.IsKeep ? "keep" : b.IsLogoErase ? "erase-logo" : "erase";
            result.Add(new ImportBox { Type = type, X = x, Y = y, W = w, H = h });
        }

        return result;
    }

    /// <summary>
    /// Removes the content inside "erase" boxes and restores "keep" boxes (logos).
    ///
    /// Each erase box is seeded in a working copy with the colour of the band of pixels
    /// just outside it (trimmed-mean of the border ring, so stray glyph pixels do not
    /// bleed in). The seed region extends beyond the box by a blur-radius margin, so
    /// when the seeded copy is blurred, box-edge pixels average seed fill on both sides
    /// instead of picking up bright text that sits just outside the box. Seeding runs
    /// twice: the second pass re-samples rings from the already-seeded image so adjacent
    /// boxes do not contaminate each other's fill colour. The blurred area is then copied
    /// back over each box and "keep" boxes are restored pixel-perfect from the original.
    /// </summary>
    private static SKImage ApplyBoxEdits(SKImage image, IReadOnlyList<ImportBox> boxes)
    {
        var erase = boxes.Where(b => !b.IsKeep).ToList();
        if (erase.Count == 0)
        {
            return image;
        }

        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var width = info.Width;
        var height = info.Height;

        using var bmp = SKBitmap.FromImage(image);
        var pm = bmp.PeekPixels() ?? throw new InvalidOperationException("Failed to read the imported pixels.");
        var span = pm.GetPixelSpan();

        var holes = new List<SKRect>();
        foreach (var box in erase)
        {
            var rect = BoxRect(box, width, height);
            // Grow each box so anti-aliased glyph edges and drop shadows land inside
            // the refilled area instead of surviving as faint outlines.
            var grow = MathF.Max(3f, MathF.Min(rect.Width, rect.Height) * 0.18f);
            holes.Add(Grow(rect, grow, width, height));
        }

        ContentAwareFill(span, width, height, UnionTouching(holes, MathF.Max(2f, MathF.Min(width, height) * 0.006f)));

        using var edited = SKImage.FromBitmap(bmp);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Failed to allocate the edit surface.");
        var canvas = surface.Canvas;
        canvas.DrawImage(edited, 0f, 0f, SKSamplingOptions.Default);

        foreach (var box in boxes.Where(b => b.IsKeep))
        {
            var rect = BoxRect(box, image.Width, image.Height);
            canvas.DrawImage(image, rect, rect, SKSamplingOptions.Default, null);
        }

        return surface.Snapshot();
    }

    // ---------------------------------------------------- content-aware erase fill

    /// <summary>
    /// Replaces every masked region with content reconstructed from its surroundings
    /// using a pull-push image pyramid, adds grain matched to the local noise level,
    /// and blends the patch back through a feathered alpha edge so the edit is invisible.
    /// </summary>
    private static void ContentAwareFill(Span<byte> span, int width, int height, IReadOnlyList<SKRect> holes)
    {
        var n = width * height;
        var mask = new bool[n];
        foreach (var hole in holes)
        {
            MarkRect(mask, width, height, hole);
        }

        var originals = span.ToArray();

        var r = new float[n];
        var g = new float[n];
        var b = new float[n];
        var wgt = new float[n];
        for (var i = 0; i < n; i++)
        {
            var o = i * 4;
            r[i] = originals[o];
            g[i] = originals[o + 1];
            b[i] = originals[o + 2];
            wgt[i] = mask[i] ? 0f : 1f;
        }

        PullPush(r, g, b, wgt, width, height);
        AddMatchedGrain(r, g, b, mask, width, height, holes, originals);

        var alpha = BuildFeatherAlpha(mask, width, height);
        for (var i = 0; i < n; i++)
        {
            var a = alpha[i];
            if (a <= 0f)
            {
                continue;
            }

            var o = i * 4;
            span[o] = (byte)MathF.Round(a * r[i] + (1f - a) * originals[o]);
            span[o + 1] = (byte)MathF.Round(a * g[i] + (1f - a) * originals[o + 1]);
            span[o + 2] = (byte)MathF.Round(a * b[i] + (1f - a) * originals[o + 2]);
        }
    }

    /// <summary>Pull-push reconstruction: repeatedly average the image down (pull),
    /// then walk back up filling unknown pixels from bilinearly interpolated coarser
    /// guesses (push). Large holes inherit colour from far away instead of smearing.</summary>
    private static void PullPush(float[] r, float[] g, float[] b, float[] wgt, int width, int height)
    {
        var levels = new List<(float[] R, float[] G, float[] B, float[] W, int Wd, int Ht)>
        {
            (r, g, b, wgt, width, height)
        };

        while (levels[^1].Wd > 1 || levels[^1].Ht > 1)
        {
            var p = levels[^1];
            var wd = Math.Max(1, p.Wd / 2);
            var ht = Math.Max(1, p.Ht / 2);
            var nr = new float[wd * ht];
            var ng = new float[wd * ht];
            var nb = new float[wd * ht];
            var nw = new float[wd * ht];
            for (var y = 0; y < ht; y++)
            {
                for (var x = 0; x < wd; x++)
                {
                    float sr = 0, sg = 0, sb = 0, sw = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var si = Math.Min(y * 2 + dy, p.Ht - 1) * p.Wd + Math.Min(x * 2 + dx, p.Wd - 1);
                            var w = p.W[si];
                            if (w <= 0)
                            {
                                continue;
                            }

                            sw += w;
                            sr += w * p.R[si];
                            sg += w * p.G[si];
                            sb += w * p.B[si];
                        }
                    }

                    if (sw > 0)
                    {
                        var di = y * wd + x;
                        nr[di] = sr / sw;
                        ng[di] = sg / sw;
                        nb[di] = sb / sw;
                        nw[di] = sw;
                    }
                }
            }

            levels.Add((nr, ng, nb, nw, wd, ht));
        }

        var top = levels[^1];
        float tr = 0, tg = 0, tb = 0, tw = 0;
        for (var i = 0; i < top.W.Length; i++)
        {
            if (top.W[i] > 0)
            {
                tw += top.W[i];
                tr += top.R[i] * top.W[i];
                tg += top.G[i] * top.W[i];
                tb += top.B[i] * top.W[i];
            }
        }

        if (tw <= 0)
        {
            tr = tg = tb = 128f;
            tw = 1f;
        }

        for (var i = 0; i < top.W.Length; i++)
        {
            if (top.W[i] <= 0)
            {
                top.R[i] = tr / tw;
                top.G[i] = tg / tw;
                top.B[i] = tb / tw;
                top.W[i] = 1f;
            }
        }

        for (var li = levels.Count - 2; li >= 0; li--)
        {
            var f = levels[li];
            var c = levels[li + 1];
            for (var y = 0; y < f.Ht; y++)
            {
                for (var x = 0; x < f.Wd; x++)
                {
                    var fi = y * f.Wd + x;
                    if (f.W[fi] > 0)
                    {
                        continue;
                    }

                    var cx = (x + 0.5f) / 2f - 0.5f;
                    var cy = (y + 0.5f) / 2f - 0.5f;
                    var x0 = (int)MathF.Floor(cx);
                    var y0 = (int)MathF.Floor(cy);
                    var tx = cx - x0;
                    var ty = cy - y0;
                    float sr = 0, sg = 0, sb = 0, sw = 0;
                    for (var dy = 0; dy <= 1; dy++)
                    {
                        for (var dx = 0; dx <= 1; dx++)
                        {
                            var ci = Math.Clamp(y0 + dy, 0, c.Ht - 1) * c.Wd + Math.Clamp(x0 + dx, 0, c.Wd - 1);
                            var w = c.W[ci] * (dx == 0 ? 1f - tx : tx) * (dy == 0 ? 1f - ty : ty);
                            if (w <= 0)
                            {
                                continue;
                            }

                            sw += w;
                            sr += w * c.R[ci];
                            sg += w * c.G[ci];
                            sb += w * c.B[ci];
                        }
                    }

                    if (sw > 0)
                    {
                        f.R[fi] = sr / sw;
                        f.G[fi] = sg / sw;
                        f.B[fi] = sb / sw;
                        f.W[fi] = 1f;
                    }
                }
            }
        }
    }

    /// <summary>Sprinkles gaussian noise into the filled areas with an amplitude taken
    /// from the ring of original pixels around each hole, so patches share the
    /// background's texture instead of looking airbrushed. Seeded for determinism.</summary>
    private static void AddMatchedGrain(
        float[] r, float[] g, float[] b, bool[] mask, int width, int height,
        IReadOnlyList<SKRect> holes, byte[] originals)
    {
        var rng = new Random(20260823);
        foreach (var hole in holes)
        {
            var (sr, sg, sb) = RingNoise(originals, width, height, hole);
            if (sr <= 0f && sg <= 0f && sb <= 0f)
            {
                continue;
            }

            var x0 = Math.Max(0, (int)hole.Left);
            var x1 = Math.Min(width - 1, (int)MathF.Ceiling(hole.Right) - 1);
            var y0 = Math.Max(0, (int)hole.Top);
            var y1 = Math.Min(height - 1, (int)MathF.Ceiling(hole.Bottom) - 1);
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var i = y * width + x;
                    if (!mask[i])
                    {
                        continue;
                    }

                    r[i] = Math.Clamp(r[i] + Grain(rng) * sr, 0f, 255f);
                    g[i] = Math.Clamp(g[i] + Grain(rng) * sg, 0f, 255f);
                    b[i] = Math.Clamp(b[i] + Grain(rng) * sb, 0f, 255f);
                }
            }
        }
    }

    /// <summary>Per-channel standard deviation of the band of original pixels just
    /// outside <paramref name="rect"/> - zero when the surrounding area is flat.</summary>
    private static (float R, float G, float B) RingNoise(byte[] px, int width, int height, SKRect rect)
    {
        var thick = Math.Max(3, (int)(Math.Min(rect.Width, rect.Height) * 0.08f));
        var x0 = Math.Max(0, (int)rect.Left - thick);
        var x1 = Math.Min(width - 1, (int)rect.Right + thick);
        var y0 = Math.Max(0, (int)rect.Top - thick);
        var y1 = Math.Min(height - 1, (int)rect.Bottom + thick);
        var ix0 = Math.Max(0, (int)rect.Left);
        var ix1 = Math.Min(width - 1, (int)MathF.Ceiling(rect.Right) - 1);
        var iy0 = Math.Max(0, (int)rect.Top);
        var iy1 = Math.Min(height - 1, (int)MathF.Ceiling(rect.Bottom) - 1);

        double sr = 0, sg = 0, sb = 0, qr = 0, qg = 0, qb = 0;
        long count = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (x >= ix0 && x <= ix1 && y >= iy0 && y <= iy1)
                {
                    continue;
                }

                var o = (y * width + x) * 4;
                sr += px[o];
                qr += (double)px[o] * px[o];
                sg += px[o + 1];
                qg += (double)px[o + 1] * px[o + 1];
                sb += px[o + 2];
                qb += (double)px[o + 2] * px[o + 2];
                count++;
            }
        }

        if (count < 16)
        {
            return (0f, 0f, 0f);
        }

        return (
            MathF.Sqrt(MathF.Max(0f, (float)(qr / count - (sr / count) * (sr / count)))),
            MathF.Sqrt(MathF.Max(0f, (float)(qg / count - (sg / count) * (sg / count)))),
            MathF.Sqrt(MathF.Max(0f, (float)(qb / count - (sb / count) * (sb / count)))));
    }

    private static float Grain(Random rng) =>
        (float)(rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 1.4f;

    /// <summary>1 inside the holes, blurred a few pixels so the composite edge fades
    /// out instead of ending on a hard rectangle border.</summary>
    private static float[] BuildFeatherAlpha(bool[] mask, int width, int height)
    {
        var alpha = new float[mask.Length];
        for (var i = 0; i < mask.Length; i++)
        {
            alpha[i] = mask[i] ? 1f : 0f;
        }

        BoxBlur(alpha, width, height);
        BoxBlur(alpha, width, height);
        return alpha;
    }

    private static void BoxBlur(float[] v, int width, int height)
    {
        const int rad = 2;
        var tmp = new float[v.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                float sum = 0;
                var n = 0;
                for (var d = -rad; d <= rad; d++)
                {
                    var xx = x + d;
                    if ((uint)xx < (uint)width)
                    {
                        sum += v[row + xx];
                        n++;
                    }
                }

                tmp[row + x] = sum / n;
            }
        }

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                float sum = 0;
                var n = 0;
                for (var d = -rad; d <= rad; d++)
                {
                    var yy = y + d;
                    if ((uint)yy < (uint)height)
                    {
                        sum += tmp[yy * width + x];
                        n++;
                    }
                }

                v[y * width + x] = sum / n;
            }
        }
    }

    private static SKRect Grow(SKRect rect, float amount, int width, int height) =>
        SKRect.Create(
            MathF.Max(0f, rect.Left - amount),
            MathF.Max(0f, rect.Top - amount),
            MathF.Min(width, rect.Right + amount) - MathF.Max(0f, rect.Left - amount),
            MathF.Min(height, rect.Bottom + amount) - MathF.Max(0f, rect.Top - amount));

    /// <summary>Unions rectangles whose gap is smaller than <paramref name="gap"/> so
    /// neighbouring detections refill as one continuous patch without seams.</summary>
    private static List<SKRect> UnionTouching(List<SKRect> rects, float gap)
    {
        var merged = new List<SKRect>(rects);
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < merged.Count && !changed; i++)
            {
                for (var j = i + 1; j < merged.Count; j++)
                {
                    if (!Grow(merged[i], gap, int.MaxValue, int.MaxValue).IntersectsWith(merged[j]))
                    {
                        continue;
                    }

                    merged[i] = SKRect.Union(merged[i], merged[j]);
                    merged.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        return merged;
    }

    private static void MarkRect(bool[] mask, int width, int height, SKRect rect)
    {
        var x0 = Math.Max(0, (int)rect.Left);
        var x1 = Math.Min(width - 1, (int)MathF.Ceiling(rect.Right) - 1);
        var y0 = Math.Max(0, (int)rect.Top);
        var y1 = Math.Min(height - 1, (int)MathF.Ceiling(rect.Bottom) - 1);
        for (var y = y0; y <= y1; y++)
        {
            var row = y * width;
            for (var x = x0; x <= x1; x++)
            {
                mask[row + x] = true;
            }
        }
    }

    private static SKRect BoxRect(ImportBox box, int width, int height) =>
        SKRect.Create(box.X * width, box.Y * height, box.W * width, box.H * height);

    private static string BuildRegionsJson(IReadOnlyList<ImportBox> boxes)
    {
        // Logo erases are removed from the background but do not become text zones.
        var erase = boxes.Where(b => !b.IsKeep && !b.IsLogoErase).ToList();
        if (erase.Count == 0)
        {
            return DefaultRegionsJson();
        }

        var keys = new[] { "header", "date", "title", "caption", "values", "footer" };
        var regions = new List<object>();
        var ordered = erase.OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var b = ordered[i];
            var key = i < keys.Length ? keys[i] : "caption";
            var fontSize = Math.Clamp(b.H * 0.4f, 0.014f, 0.055f);
            regions.Add(new
            {
                Key = key,
                X = Round3(b.X),
                Y = Round3(b.Y),
                W = Round3(b.W),
                H = Round3(b.H),
                FontSize = Round3(fontSize),
                Align = "center",
                Bold = key is "header" or "title" or "values"
            });
        }

        return JsonSerializer.Serialize(regions);
    }

    private static float Round3(float v) => MathF.Round(v, 3);

    // ------------------------------------------------------------- auto detection

    private sealed class CellStats
    {
        public int Width;
        public int Height;
        public int Cols;
        public int Rows;
        public float[] Variance = Array.Empty<float>();
        public float[] Saturation = Array.Empty<float>();
    }

    /// <summary>Downscales the image onto a coarse grid and computes per-cell luminance
    /// variance and mean colour saturation - shared by the text and logo detectors.</summary>
    private static CellStats ComputeCellStats(SKImage image, int targetWidth)
    {
        const int cell = 5;
        var w = Math.Min(targetWidth, image.Width);
        var h = Math.Max(1, (int)Math.Round(image.Height * (w / (float)image.Width)));
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var small = SKSurface.Create(info);
        small.Canvas.DrawImage(image, new SKRect(0, 0, w, h), SKSamplingOptions.Default, null);
        using var pm = small.PeekPixels();
        var span = pm.GetPixelSpan();

        var lum = new float[w * h];
        var sat = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var r = span[i];
                var g = span[i + 1];
                var b = span[i + 2];
                lum[y * w + x] = 0.299f * r + 0.587f * g + 0.114f * b;
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                sat[y * w + x] = max - min;
            }
        }

        var cols = w / cell;
        var rows = h / cell;
        var stats = new CellStats { Width = w, Height = h, Cols = cols, Rows = rows };
        if (cols < 2 || rows < 2)
        {
            return stats;
        }

        stats.Variance = new float[cols * rows];
        stats.Saturation = new float[cols * rows];
        for (var cy = 0; cy < rows; cy++)
        {
            for (var cx = 0; cx < cols; cx++)
            {
                float mean = 0, m2 = 0, s = 0;
                var n = 0;
                for (var dy = 0; dy < cell; dy++)
                {
                    for (var dx = 0; dx < cell; dx++)
                    {
                        var px = cx * cell + dx;
                        var py = cy * cell + dy;
                        if (px >= w || py >= h)
                        {
                            continue;
                        }

                        var v = lum[py * w + px];
                        mean += v;
                        m2 += v * v;
                        s += sat[py * w + px];
                        n++;
                    }
                }

                if (n == 0)
                {
                    continue;
                }

                var idx = cy * cols + cx;
                mean /= n;
                m2 /= n;
                stats.Variance[idx] = m2 - mean * mean;
                stats.Saturation[idx] = s / n;
            }
        }

        return stats;
    }

    /// <summary>
    /// Text heuristic run at two scales (coarse catches large headings, fine catches
    /// small lines): mark cells with high luminance variance and low colour saturation,
    /// group adjacent cells into blocks, drop blocks overlapping a detected logo, then
    /// merge overlapping boxes from both scales.
    /// </summary>
    private static IReadOnlyList<ImportBox> DetectTextBlocks(SKImage image, IReadOnlyList<ImportBox> logoBoxes)
    {
        var candidates = new List<ImportBox>();
        candidates.AddRange(DetectTextBlocksAtScale(image, 96, logoBoxes));
        candidates.AddRange(DetectTextBlocksAtScale(image, 192, logoBoxes));
        return MergeLineFragments(MergeBoxes(candidates));
    }

    /// <summary>Unions erase boxes separated only by a sliver of background (letter
    /// columns or line fragments of one heading) so they refill as a single seamless
    /// patch instead of two neighbouring rectangles with different fills.</summary>
    private static List<ImportBox> MergeLineFragments(List<ImportBox> boxes)
    {
        const float gap = 0.015f;
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < boxes.Count && !changed; i++)
            {
                for (var j = i + 1; j < boxes.Count; j++)
                {
                    if (!GapTouch(boxes[i], boxes[j], gap))
                    {
                        continue;
                    }

                    var x1 = MathF.Min(boxes[i].X, boxes[j].X);
                    var y1 = MathF.Min(boxes[i].Y, boxes[j].Y);
                    var x2 = MathF.Max(boxes[i].X + boxes[i].W, boxes[j].X + boxes[j].W);
                    var y2 = MathF.Max(boxes[i].Y + boxes[i].H, boxes[j].Y + boxes[j].H);
                    boxes[i] = new ImportBox
                    {
                        Type = boxes[i].Type,
                        X = Round3(x1),
                        Y = Round3(y1),
                        W = Round3(x2 - x1),
                        H = Round3(y2 - y1)
                    };
                    boxes.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        return boxes;
    }

    private static bool GapTouch(ImportBox a, ImportBox b, float gap) =>
        a.X - gap < b.X + b.W && b.X - gap < a.X + a.W &&
        a.Y - gap < b.Y + b.H && b.Y - gap < a.Y + a.H;

    private static IReadOnlyList<ImportBox> DetectTextBlocksAtScale(
        SKImage image, int targetWidth, IReadOnlyList<ImportBox> logoBoxes)
    {
        const int cell = 5;
        var s = ComputeCellStats(image, targetWidth);
        if (s.Cols < 2 || s.Rows < 2)
        {
            return Array.Empty<ImportBox>();
        }

        var vars = s.Variance;
        var meanVar = vars.Average();
        var stdVar = (float)Math.Sqrt(vars.Average(v => (v - meanVar) * (v - meanVar)));
        var threshold = meanVar + Math.Max(70f, stdVar * 1.1f);

        var marked = new bool[s.Cols * s.Rows];
        for (var i = 0; i < marked.Length; i++)
        {
            if (vars[i] > threshold && s.Saturation[i] < 125)
            {
                marked[i] = true;
            }
        }

        var result = new List<ImportBox>();
        foreach (var group in GroupCells(marked, s.Cols, s.Rows))
        {
            var box = ToImportBox(group, cell, s.Width, s.Height, minArea: 0.006f, type: "erase");
            if (box is not null && !logoBoxes.Any(l => OverlapRatio(box, l) > 0.4f))
            {
                result.Add(box);
            }
        }

        return result;
    }

    /// <summary>Greedy union of overlapping boxes (largest first) so multi-scale
    /// detections of the same text block collapse into one region.</summary>
    private static List<ImportBox> MergeBoxes(List<ImportBox> boxes)
    {
        var result = new List<ImportBox>();
        foreach (var b in boxes.OrderByDescending(b => b.W * b.H))
        {
            ImportBox? hit = null;
            foreach (var r in result)
            {
                if (OverlapRatio(r, b) > 0.3f || OverlapRatio(b, r) > 0.3f)
                {
                    hit = r;
                    break;
                }
            }

            if (hit is null)
            {
                result.Add(new ImportBox { Type = b.Type, X = b.X, Y = b.Y, W = b.W, H = b.H });
            }
            else
            {
                var x1 = MathF.Min(hit.X, b.X);
                var y1 = MathF.Min(hit.Y, b.Y);
                var x2 = MathF.Max(hit.X + hit.W, b.X + b.W);
                var y2 = MathF.Max(hit.Y + hit.H, b.Y + b.H);
                hit.X = Round3(x1);
                hit.Y = Round3(y1);
                hit.W = Round3(x2 - x1);
                hit.H = Round3(y2 - y1);
            }
        }

        return result;
    }

    /// <summary>
    /// Logo heuristic: logos are compact colourful artwork, so mark cells with high
    /// colour saturation, group them, and keep only reasonably sized groups (a full-bleed
    /// coloured background or a tiny speck is not a logo).
    /// </summary>
    private static IReadOnlyList<ImportBox> DetectLogoBlocks(SKImage image)
    {
        const int cell = 5;
        const float saturationThreshold = 90f;
        var s = ComputeCellStats(image, 96);
        if (s.Cols < 2 || s.Rows < 2)
        {
            return Array.Empty<ImportBox>();
        }

        var marked = new bool[s.Cols * s.Rows];
        for (var i = 0; i < marked.Length; i++)
        {
            if (s.Saturation[i] >= saturationThreshold)
            {
                marked[i] = true;
            }
        }

        var result = new List<ImportBox>();
        foreach (var group in GroupCells(marked, s.Cols, s.Rows))
        {
            var box = ToImportBox(group, cell, s.Width, s.Height, minArea: 0.004f, type: "erase-logo");
            if (box is not null && box.W * box.H <= 0.30f)
            {
                result.Add(box);
            }
        }

        return result;
    }

    private static ImportBox? ToImportBox(
        (int MinX, int MinY, int MaxX, int MaxY) group, int cell, int w, int h, float minArea, string type)
    {
        var x0 = group.MinX * cell / (float)w;
        var y0 = group.MinY * cell / (float)h;
        var x1 = (group.MaxX + 1) * cell / (float)w;
        var y1 = (group.MaxY + 1) * cell / (float)h;
        var bw = x1 - x0;
        var bh = y1 - y0;
        if (bw * bh < minArea)
        {
            return null;
        }

        return new ImportBox
        {
            Type = type,
            X = Round3(Math.Clamp(x0, 0, 0.99f)),
            Y = Round3(Math.Clamp(y0, 0, 0.99f)),
            W = Round3(Math.Clamp(bw, 0.02f, 1)),
            H = Round3(Math.Clamp(bh, 0.02f, 1))
        };
    }

    /// <summary>Flood-fills adjacent marked cells and returns their bounding boxes.</summary>
    private static List<(int MinX, int MinY, int MaxX, int MaxY)> GroupCells(bool[] marked, int cols, int rows)
    {
        var boxes = new List<(int MinX, int MinY, int MaxX, int MaxY)>();
        var visited = new bool[cols * rows];
        var stack = new Stack<int>();
        for (var start = 0; start < cols * rows; start++)
        {
            if (!marked[start] || visited[start])
            {
                continue;
            }

            stack.Push(start);
            visited[start] = true;
            var minX = cols;
            var minY = rows;
            var maxX = -1;
            var maxY = -1;
            var cellCount = 0;
            while (stack.Count > 0)
            {
                var idx = stack.Pop();
                var cy = idx / cols;
                var cx = idx % cols;
                minX = Math.Min(minX, cx);
                maxX = Math.Max(maxX, cx);
                minY = Math.Min(minY, cy);
                maxY = Math.Max(maxY, cy);
                cellCount++;

                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = cx + dx;
                        var ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= cols || ny >= rows)
                        {
                            continue;
                        }

                        var ni = ny * cols + nx;
                        if (marked[ni] && !visited[ni])
                        {
                            visited[ni] = true;
                            stack.Push(ni);
                        }
                    }
                }
            }

            if (cellCount >= 2)
            {
                boxes.Add((minX, minY, maxX, maxY));
            }
        }

        return boxes;
    }

    /// <summary>Intersection area of two boxes divided by the first box's area.</summary>
    private static float OverlapRatio(ImportBox a, ImportBox b)
    {
        var x1 = MathF.Max(a.X, b.X);
        var y1 = MathF.Max(a.Y, b.Y);
        var x2 = MathF.Min(a.X + a.W, b.X + b.W);
        var y2 = MathF.Min(a.Y + a.H, b.Y + b.H);
        var inter = MathF.Max(0f, x2 - x1) * MathF.Max(0f, y2 - y1);
        return inter / MathF.Max(0.0001f, a.W * a.H);
    }

    // --------------------------------------------------------------- persistence

    /// <summary>Stores the untouched upload so the layout can be re-edited later.</summary>
    private async Task<string?> SaveOriginalAsync(int tenantId, byte[] bytes, string ext, CancellationToken ct)
    {
        var webRoot = _env.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return null;
        }

        var dir = Path.Combine(webRoot, "templates", "imports", tenantId.ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var fileName = $"orig_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6]}{ext}";
            var fullPath = Path.Combine(dir, fileName);
            await File.WriteAllBytesAsync(fullPath, bytes, ct);
            return $"/templates/imports/{tenantId}/{fileName}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store the original upload; re-editing stays unavailable for this template.");
            return null;
        }
    }

    /// <summary>Deletes a replaced file under /templates/imports (never touches other paths).</summary>
    private void DeleteImportedFile(string? relativePath, string? keepPath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            string.Equals(relativePath, keepPath, StringComparison.OrdinalIgnoreCase) ||
            !relativePath.StartsWith("/templates/imports/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var webRoot = _env.WebRootPath;
            if (string.IsNullOrWhiteSpace(webRoot))
            {
                return;
            }

            var full = Path.Combine(webRoot, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete the replaced import file {Path}.", relativePath);
        }
    }

    private async Task<string?> SaveBackgroundAsync(int tenantId, SKImage image, string originalExt, CancellationToken ct)
    {
        var webRoot = _env.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return null;
        }

        var dir = Path.Combine(webRoot, "templates", "imports", tenantId.ToString());
        Directory.CreateDirectory(dir);

        // Downscale wide uploads so the stored layout stays reasonable.
        var maxWidth = 1080;
        SKImage toSave = image;
        if (image.Width > maxWidth)
        {
            var scale = maxWidth / (float)image.Width;
            var info = new SKImageInfo(maxWidth, (int)(image.Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            surface.Canvas.DrawImage(image, new SKRect(0, 0, info.Width, info.Height), SKSamplingOptions.Default, null);
            toSave = surface.Snapshot();
        }

        try
        {
            using var data = toSave.Encode(SKEncodedImageFormat.Png, 90);
            if (data is null || data.Size == 0)
            {
                return null;
            }

            var fileName = $"bg_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6]}.png";
            var fullPath = Path.Combine(dir, fileName);
            await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            data.SaveTo(fs);
            return $"/templates/imports/{tenantId}/{fileName}";
        }
        finally
        {
            if (!ReferenceEquals(toSave, image))
            {
                toSave.Dispose();
            }
        }
    }

    /// <summary>Samples the image and returns an accent hex plus a readable text hex.</summary>
    private static (string Accent, string TextColor) AnalyzeColors(SKImage image)
    {
        const int sampleSize = 24;
        using var small = SKSurface.Create(new SKImageInfo(sampleSize, sampleSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        small.Canvas.DrawImage(image, new SKRect(0, 0, sampleSize, sampleSize), SKSamplingOptions.Default, null);
        using var pm = small.PeekPixels();
        var span = pm.GetPixelSpan();

        // Quantise each channel to 2 bits (64 buckets) and tally.
        var buckets = new Dictionary<int, (int R, int G, int B, int Count)>();
        long sumR = 0, sumG = 0, sumB = 0;
        var n = 0;
        for (var i = 0; i + 3 < span.Length; i += 4)
        {
            var r = span[i];
            var g = span[i + 1];
            var b = span[i + 2];
            sumR += r;
            sumG += g;
            sumB += b;
            n++;

            var key = (r >> 6) << 4 | (g >> 6) << 2 | (b >> 6);
            buckets.TryGetValue(key, out var cur);
            buckets[key] = (cur.R + r, cur.G + g, cur.B + b, cur.Count + 1);
        }

        // Prefer the most frequent colourful bucket for the accent.
        SKColor accent = new(255, 102, 0);
        var bestScore = -1;
        foreach (var kv in buckets)
        {
            if (kv.Value.Count < 2)
            {
                continue;
            }

            var r = kv.Value.R / kv.Value.Count;
            var g = kv.Value.G / kv.Value.Count;
            var b = kv.Value.B / kv.Value.Count;
            var saturation = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            var score = kv.Value.Count * 10 + (saturation >= 40 ? 1 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                accent = new SKColor((byte)r, (byte)g, (byte)b);
            }
        }

        if (HexColor.Luminance(accent) < 120)
        {
            // Dark accent: brighten it a little so it reads on dark backgrounds.
            accent = new SKColor(
                (byte)Math.Min(255, accent.Red + 70),
                (byte)Math.Min(255, accent.Green + 70),
                (byte)Math.Min(255, accent.Blue + 70));
        }

        var avgBg = new SKColor((byte)(sumR / Math.Max(1, n)), (byte)(sumG / Math.Max(1, n)), (byte)(sumB / Math.Max(1, n)));
        return (HexColor.ToHex(accent), HexColor.ToHex(HexColor.AutoTextColor(avgBg)));
    }

    private static string DefaultRegionsJson() => JsonSerializer.Serialize(new[]
    {
        new { Key = "header", X = 0.05, Y = 0.05, W = 0.9, H = 0.07, FontSize = 0.028, Align = "center", Bold = true },
        new { Key = "date", X = 0.05, Y = 0.14, W = 0.9, H = 0.045, FontSize = 0.019, Align = "center", Bold = false },
        new { Key = "title", X = 0.08, Y = 0.22, W = 0.84, H = 0.2, FontSize = 0.048, Align = "center", Bold = true },
        new { Key = "caption", X = 0.12, Y = 0.46, W = 0.76, H = 0.16, FontSize = 0.026, Align = "center", Bold = false },
        new { Key = "values", X = 0.06, Y = 0.88, W = 0.88, H = 0.06, FontSize = 0.021, Align = "center", Bold = true },
        new { Key = "footer", X = 0.06, Y = 0.945, W = 0.88, H = 0.04, FontSize = 0.016, Align = "center", Bold = false }
    });
}