using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class Coupon
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string Code { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    public CouponType Type { get; set; } = CouponType.Percent;

    public decimal Value { get; set; }

    public int? MaxRedemptions { get; set; }

    public int RedemptionCount { get; set; }

    public DateTime? ValidFrom { get; set; }

    public DateTime? ValidUntil { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();

    public ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
}
