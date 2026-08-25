using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class Payment
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public int? InvoiceId { get; set; }

    public Invoice? Invoice { get; set; }

    [Required, StringLength(50)]
    public string Gateway { get; set; } = "mock";

    [StringLength(200)]
    public string? GatewayPaymentId { get; set; }

    [StringLength(200)]
    public string? GatewayOrderId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "INR";

    public PaymentStatus Status { get; set; } = PaymentStatus.Initiated;

    [StringLength(500)]
    public string? FailureReason { get; set; }

    [StringLength(50)]
    public string? Method { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PaidAt { get; set; }
}
