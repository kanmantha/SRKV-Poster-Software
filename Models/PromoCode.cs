using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class PromoCode
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string Code { get; set; } = string.Empty;

    public PromoType Type { get; set; } = PromoType.PercentOff;

    public decimal Value { get; set; }

    public int FreeMonths { get; set; }

    public int? MaxRedemptions { get; set; }

    public int RedemptionCount { get; set; }

    public DateTime? ValidFrom { get; set; }

    public DateTime? ValidUntil { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
}
