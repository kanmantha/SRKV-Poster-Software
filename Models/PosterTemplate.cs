using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

/// <summary>
/// A reusable visual design for posters. System templates (TenantId = 0) ship with
/// the product; tenants can create their own. The Theme + AccentColor drive the
/// SkiaSharp renderer, and today's events are always rendered onto the template.
/// </summary>
public class PosterTemplate
{
    public int Id { get; set; }

    /// <summary>0 = system/global template shared by every tenant.</summary>
    public int TenantId { get; set; } = 0;

    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }

    public bool IsSystem { get; set; }

    /// <summary>Business sector this template is designed for (see SectorCatalog).</summary>
    [StringLength(50)]
    public string Sector { get; set; } = SectorCatalog.General;

    public bool IsActive { get; set; } = true;

    /// <summary>True when the template was created by uploading an existing poster.</summary>
    public bool IsImported { get; set; }

    /// <summary>Relative path (under wwwroot) of the uploaded background layout image.</summary>
    [StringLength(500)]
    public string? BackgroundImagePath { get; set; }

    /// <summary>Relative path of the untouched original upload, kept so imported layouts can be re-edited.</summary>
    [StringLength(500)]
    public string? OriginalBackgroundPath { get; set; }

    /// <summary>JSON array of import boxes (erase / erase-logo / keep) applied to the original upload.</summary>
    public string? ImportBoxesJson { get; set; }

    /// <summary>JSON PosterTreatmentRequest baked into the current background; re-applied on re-edits.</summary>
    public string? TreatmentJson { get; set; }

    /// <summary>Optional hex text colour used on top of the background image.</summary>
    [StringLength(20)]
    public string? TextColor { get; set; }

    /// <summary>JSON array of text regions (fractions of the canvas) for imported layouts.</summary>
    public string? TextRegionsJson { get; set; }

    /// <summary>Dark overlay percentage (0-90) applied to the background for readability.</summary>
    public int BackgroundDim { get; set; } = 35;

    /// <summary>Visual theme key: srv | colorful | light | dark | auto.</summary>
    [StringLength(50)]
    public string Theme { get; set; } = "auto";

    /// <summary>Optional hex accent color override, e.g. "#FF6600".</summary>
    [StringLength(20)]
    public string? AccentColor { get; set; }

    [StringLength(500)]
    public string? ThumbnailPath { get; set; }

    /// <summary>Rendered thumbnail PNG persisted in the database so the template gallery
    /// survives ephemeral disks (Render wipes wwwroot on every deploy/restart).</summary>
    public byte[]? ThumbnailBytes { get; set; }

    /// <summary>Where the organisation logo is overlaid on posters made with this template.
    /// One of: top-left | top-center | top-right | middle-left | center | middle-right |
    /// bottom-left | bottom-center | bottom-right | none.</summary>
    [StringLength(20)]
    public string LogoPosition { get; set; } = "top-right";

    /// <summary>Uploaded per-template logo image bytes (PNG/JPG/WEBP) persisted in the
    /// database so it survives Render's ephemeral disk, drawn on the template's posters.</summary>
    public byte[]? LogoBytes { get; set; }

    /// <summary>MIME type of the uploaded per-template logo, e.g. "image/png".</summary>
    [StringLength(100)]
    public string? LogoMime { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}