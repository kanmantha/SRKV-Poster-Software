using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class PosterEvent
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public Tenant Tenant { get; set; } = null!;

    [Required, StringLength(500)]
    public string Text { get; set; } = string.Empty;

    public int? Year { get; set; }

    [StringLength(50)]
    public string Kind { get; set; } = "event";

    [StringLength(500)]
    public string? Url { get; set; }

    public int PosterId { get; set; }

    public Poster Poster { get; set; } = null!;
}
