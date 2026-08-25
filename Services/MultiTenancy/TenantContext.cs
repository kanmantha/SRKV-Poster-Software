using DailyPosterGenerator.Models;

namespace DailyPosterGenerator.Services.MultiTenancy;

/// <summary>
/// Scoped per-request tenant state. Populated by the tenant-resolution middleware
/// from the authenticated JWT (defaults to the school tenant id 1 when anonymous).
/// </summary>
public sealed class TenantContext
{
    public int TenantId { get; set; } = 1;

    public bool IsAuthenticated { get; set; }

    public int? UserId { get; set; }

    public string? UserEmail { get; set; }

    public bool IsAdmin { get; set; }

    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.Active;

    public string? PlanCode { get; set; }

    public int CreditsRemaining { get; set; }

    public DateTime? PeriodEnd { get; set; }

    public DateTime? TrialEndsAt { get; set; }
}
