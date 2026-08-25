using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class SubscriptionPlan
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string Code { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }

    public decimal PricePerMonth { get; set; }

    public decimal PricePerYear { get; set; }

    public string Currency { get; set; } = "INR";

    public int MonthlyCreditAllowance { get; set; }

    public int MaxUsers { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsDefault { get; set; }

    public int SortOrder { get; set; }

    public bool AllowsAiGeneration { get; set; }

    public bool AllowsAiImageGeneration { get; set; }

    public bool AllowsBackgroundRemoval { get; set; }

    public bool AllowsUpscale { get; set; }

    public bool AllowsContentRewrite { get; set; }

    public bool AllowsCustomBranding { get; set; }

    public bool AllowsExport { get; set; }

    public bool AllowsPublishing { get; set; }

    public bool AllowsPrioritySupport { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
