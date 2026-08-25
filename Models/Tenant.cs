using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class Tenant
{
    public int Id { get; set; }

    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [StringLength(100)]
    public string? Slug { get; set; }

    /// <summary>Business sector (see SectorCatalog). Drives default templates.</summary>
    [StringLength(50)]
    public string Sector { get; set; } = SectorCatalog.General;

    /// <summary>Relative wwwroot path of the tenant's logo, drawn onto generated posters.</summary>
    [StringLength(300)]
    public string? LogoPath { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AppUser> Users { get; set; } = new List<AppUser>();

    public ICollection<Poster> Posters { get; set; } = new List<Poster>();

    public ICollection<SystemSetting> Settings { get; set; } = new List<SystemSetting>();
}
