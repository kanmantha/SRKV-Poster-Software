using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class AppUser
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    public bool EmailConfirmed { get; set; }

    public bool IsAdmin { get; set; }

    [StringLength(200)]
    public string? EmailVerificationToken { get; set; }

    public DateTime? EmailVerificationTokenExpires { get; set; }

    [StringLength(200)]
    public string? PasswordResetToken { get; set; }

    public DateTime? PasswordResetTokenExpires { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    public ICollection<UsageHistory> Usage { get; set; } = new List<UsageHistory>();
}
