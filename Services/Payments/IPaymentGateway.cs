namespace DailyPosterGenerator.Services.Payments;

public record PaymentRequest(
    string Reference,
    decimal Amount,
    string Currency,
    string Description,
    string? GatewayOrderId = null);

public record PaymentResult(
    bool Success,
    string? PaymentId,
    string? OrderId,
    string? Error);

public interface IPaymentGateway
{
    string Name { get; }

    Task<PaymentResult> CreatePaymentAsync(PaymentRequest request, CancellationToken ct = default);
}

/// <summary>
/// Development gateway that always succeeds. Razorpay is implemented against this
/// same interface in the payments phase (see RazorpayPaymentGateway).
/// </summary>
public class MockPaymentGateway : IPaymentGateway
{
    public string Name => "mock";

    public Task<PaymentResult> CreatePaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        var id = $"mock_{Guid.NewGuid():N}";
        return Task.FromResult(new PaymentResult(
            true,
            id,
            request.GatewayOrderId ?? $"ord_{Guid.NewGuid():N}"[..22],
            null));
    }
}
