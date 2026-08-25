using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class Subscription
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public int PlanId { get; set; }

    public SubscriptionPlan Plan { get; set; } = null!;

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;

    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;

    public DateTime StartDate { get; set; } = DateTime.UtcNow;

    public DateTime? CurrentPeriodStart { get; set; }

    public DateTime? CurrentPeriodEnd { get; set; }

    public DateTime? CancelAtPeriodEnd { get; set; }

    public DateTime? CancelledAt { get; set; }

    public DateTime? TrialEndsAt { get; set; }

    public int CreditsRemaining { get; set; }

    public int? PromoCodeId { get; set; }

    public PromoCode? PromoCode { get; set; }

    public int? CouponId { get; set; }

    public Coupon? Coupon { get; set; }

    public decimal? DiscountAmount { get; set; }

    [StringLength(200)]
    public string? GatewaySubscriptionId { get; set; }

    [StringLength(200)]
    public string? GatewayCustomerId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}
