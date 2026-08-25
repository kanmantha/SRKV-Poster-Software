using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class Invoice
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    [Required, StringLength(50)]
    public string InvoiceNumber { get; set; } = string.Empty;

    public int? SubscriptionId { get; set; }

    public Subscription? Subscription { get; set; }

    public int? CouponId { get; set; }

    public Coupon? Coupon { get; set; }

    public DateTime IssueDate { get; set; } = DateTime.UtcNow;

    public DateTime? DueDate { get; set; }

    public decimal Subtotal { get; set; }

    public decimal Discount { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal Total { get; set; }

    public string Currency { get; set; } = "INR";

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Pending;

    [StringLength(200)]
    public string? RazorpayOrderId { get; set; }

    [StringLength(100)]
    public string? PaymentMethod { get; set; }

    [StringLength(200)]
    public string? BillingName { get; set; }

    [StringLength(200)]
    public string? BillingEmail { get; set; }

    [StringLength(50)]
    public string? BillingGstin { get; set; }

    [StringLength(500)]
    public string? BillingAddress { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
