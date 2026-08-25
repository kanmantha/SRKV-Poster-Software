using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class UsageHistory
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public int? UserId { get; set; }

    public AppUser? User { get; set; }

    [Required, StringLength(50)]
    public string Feature { get; set; } = string.Empty;

    public int CreditsSpent { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
