using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public enum PosterStatus
{
    Draft = 0,
    Ready = 1,
    Published = 2,
    Failed = 3
}

public enum PosterSource
{
    Automatic = 0,
    Manual = 1
}

public class Poster
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public Tenant Tenant { get; set; } = null!;

    [Required, StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required, StringLength(500)]
    public string EventTitle { get; set; } = string.Empty;

    [StringLength(2000)]
    public string Description { get; set; } = string.Empty;

    [StringLength(100)]
    public string? Category { get; set; }

    public DateTime EventDate { get; set; }

    [StringLength(3000)]
    public string Caption { get; set; } = string.Empty;

    [StringLength(1000)]
    public string Hashtags { get; set; } = string.Empty;

    [StringLength(500)]
    public string? ImagePath { get; set; }

    public byte[]? ImageBytes { get; set; }

    public PosterStatus Status { get; set; } = PosterStatus.Draft;

    public PosterSource Source { get; set; } = PosterSource.Automatic;

    [StringLength(1000)]
    public string? PublishedPlatforms { get; set; }

    [StringLength(500)]
    public string? AiProvider { get; set; }

    public int? TemplateId { get; set; }

    public PosterTemplate? Template { get; set; }

    [StringLength(200)]
    public string? TemplateName { get; set; }

    [StringLength(2000)]
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? GeneratedAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    public ICollection<PosterEvent> Events { get; set; } = new List<PosterEvent>();
}
