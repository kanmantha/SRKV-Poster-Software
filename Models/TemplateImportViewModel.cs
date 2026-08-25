using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace DailyPosterGenerator.Models;

public class TemplateImportViewModel
{
    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }

    // Nullable so posting without a workspace/sector choice never blocks the import;
    // the controller falls back to the general sector.
    public string? Sector { get; set; } = SectorCatalog.General;

    [Required]
    public IFormFile? Upload { get; set; }

    /// <summary>JSON array of {Type:"erase"|"keep", X,Y,W,H} boxes in normalized 0..1 coords.</summary>
    public string? BoxesJson { get; set; }

    /// <summary>Colour refresh chosen in the wizard: original | tint | enhance | grayscale | fresh.</summary>
    [StringLength(20)]
    public string? TreatmentKind { get; set; }

    [StringLength(9)]
    public string? TintHex { get; set; }

    public float? TintStrength { get; set; }

    [StringLength(20)]
    public string? FreshTheme { get; set; }

    [StringLength(9)]
    public string? FreshAccent { get; set; }

    public IReadOnlyList<(string Value, string Label)> SectorOptions { get; } =
        SectorCatalog.All.Select(s => (s, SectorCatalog.Label(s))).ToArray();
}