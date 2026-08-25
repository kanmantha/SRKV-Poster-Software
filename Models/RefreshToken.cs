using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class RefreshToken
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public AppUser User { get; set; } = null!;

    [Required, StringLength(256)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? RevokedAt { get; set; }

    [StringLength(256)]
    public string? ReplacedByTokenHash { get; set; }

    [StringLength(64)]
    public string? CreatedByIp { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
}
