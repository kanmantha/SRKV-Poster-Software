using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services.Email;
using DailyPosterGenerator.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Services.Subscriptions;

public class SubscribeRequest
{
    public string PlanCode { get; set; } = string.Empty;
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;
    public string? PromoCode { get; set; }
    public string? CouponCode { get; set; }
    public string? PaymentMethodId { get; set; }
    public string? BillingName { get; set; }
    public string? BillingEmail { get; set; }
    public string? BillingGstin { get; set; }
    public string? BillingAddress { get; set; }
}

public record SubscriptionResult(
    bool Success,
    Subscription? Subscription,
    Invoice? Invoice,
    Payment? Payment,
    string? Error);

public interface ISubscriptionService
{
    Task<List<SubscriptionPlan>> GetPlansAsync(CancellationToken ct = default);

    Task<Subscription?> GetLatestAsync(int tenantId, CancellationToken ct = default);

    Task<Subscription?> GetActiveAsync(int tenantId, CancellationToken ct = default);

    Task<SubscriptionResult> EnsureFreeSubscriptionAsync(int tenantId, CancellationToken ct = default);

    /// <summary>Starts a trial subscription (Trialing) for a tenant on the configured trial plan.</summary>
    Task<SubscriptionResult> CreateTrialAsync(int tenantId, CancellationToken ct = default);

    Task<SubscriptionResult> SubscribeAsync(int tenantId, SubscribeRequest request, int? actorUserId = null, CancellationToken ct = default);

    Task<SubscriptionResult> ChangePlanAsync(int tenantId, string planCode, CancellationToken ct = default);

    Task<SubscriptionResult> CancelAsync(int tenantId, bool immediate = false, CancellationToken ct = default);

    Task<SubscriptionResult> RolloverCycleAsync(int tenantId, CancellationToken ct = default);

    Task<List<UsageHistory>> GetUsageAsync(int tenantId, int take = 50, CancellationToken ct = default);
}

public class SubscriptionService : ISubscriptionService
{
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly IPaymentGateway _gateway;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(
        IDbContextFactory<DailyPosterDbContext> dbFactory,
        IPaymentGateway gateway,
        IEmailService email,
        IConfiguration config,
        ILogger<SubscriptionService> logger)
    {
        _dbFactory = dbFactory;
        _gateway = gateway;
        _email = email;
        _config = config;
        _logger = logger;
    }

    public async Task<List<SubscriptionPlan>> GetPlansAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SubscriptionPlans.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);
    }

    public async Task<Subscription?> GetLatestAsync(int tenantId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Subscriptions.AsNoTracking()
            .Include(s => s.Plan)
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Subscription?> GetActiveAsync(int tenantId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Subscriptions.AsNoTracking()
            .Include(s => s.Plan)
            .Where(s => s.TenantId == tenantId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<SubscriptionResult> EnsureFreeSubscriptionAsync(int tenantId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Subscriptions
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            return new SubscriptionResult(true, existing, null, null, null);
        }

        var free = await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Code == "FREE" && p.IsActive, ct);
        if (free is null)
        {
            return new SubscriptionResult(false, null, null, null, "FREE plan is not configured.");
        }

        var subscription = CreateSubscription(tenantId, free, BillingCycle.Monthly, null, null, null);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Auto-subscribed tenant {TenantId} to FREE plan", tenantId);
        return new SubscriptionResult(true, subscription, null, null, null);
    }

    public async Task<SubscriptionResult> CreateTrialAsync(int tenantId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Subscriptions
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            return new SubscriptionResult(true, existing, null, null, null);
        }

        var trialDays = _config.GetValue<int>("SaaS:TrialDays", 14);
        var trialPlanCode = _config["SaaS:TrialPlanCode"] ?? "STARTER";

        var plan = await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Code == trialPlanCode && p.IsActive, ct)
            ?? await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.IsDefault && p.IsActive, ct);
        if (plan is null)
        {
            return new SubscriptionResult(false, null, null, null, "No plan available for the trial.");
        }

        var now = DateTime.UtcNow;
        var subscription = new Subscription
        {
            TenantId = tenantId,
            PlanId = plan.Id,
            Status = SubscriptionStatus.Trialing,
            BillingCycle = BillingCycle.Monthly,
            StartDate = now,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now.AddMonths(1),
            TrialEndsAt = now.AddDays(trialDays),
            CreditsRemaining = plan.MonthlyCreditAllowance
        };

        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Started {Days}-day trial for tenant {TenantId} on plan {PlanCode}",
            trialDays, tenantId, plan.Code);
        return new SubscriptionResult(true, subscription, null, null, null);
    }

    public async Task<SubscriptionResult> SubscribeAsync(
        int tenantId,
        SubscribeRequest request,
        int? actorUserId = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var plan = await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Code == request.PlanCode && p.IsActive, ct);
        if (plan is null)
        {
            return new SubscriptionResult(false, null, null, null, $"Plan '{request.PlanCode}' is not available.");
        }

        var active = await db.Subscriptions
            .Where(s => s.TenantId == tenantId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (active is not null)
        {
            return new SubscriptionResult(false, active, null, null,
                "An active subscription already exists. Use change-plan to switch plans.");
        }

        // Promo (recurring discount) and one-time coupon.
        var promo = await ResolvePromoAsync(db, request.PromoCode, ct);
        var coupon = await ResolveCouponAsync(db, request.CouponCode, ct);

        var basePrice = request.BillingCycle == BillingCycle.Yearly ? plan.PricePerYear : plan.PricePerMonth;
        var discount = ComputeDiscount(plan, basePrice, request.BillingCycle, promo);
        var taxable = Math.Max(0, basePrice - discount);

        var couponBonusCredits = coupon?.Type == CouponType.Credits ? (int)coupon.Value : 0;
        var couponDiscount = coupon is { Type: not CouponType.Credits }
            ? coupon.Type == CouponType.Percent ? basePrice * coupon.Value / 100m : Math.Min(coupon.Value, taxable)
            : 0m;
        discount += couponDiscount;
        taxable = Math.Max(0, basePrice - discount);

        var subscription = CreateSubscription(
            tenantId, plan, request.BillingCycle, promo, coupon,
            discount, couponBonusCredits);

        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);

        Invoice? invoice = null;
        Payment? payment = null;

        if (taxable > 0)
        {
            var gstRate = GetGstRate();
            invoice = new Invoice
            {
                TenantId = tenantId,
                SubscriptionId = subscription.Id,
                CouponId = coupon?.Id,
                InvoiceNumber = $"INV-{DateTime.UtcNow:yyyyMMdd}-{subscription.Id:D4}",
                IssueDate = DateTime.UtcNow,
                DueDate = DateTime.UtcNow.AddDays(7),
                Subtotal = basePrice,
                Discount = discount,
                TaxRate = gstRate,
                TaxAmount = taxable * gstRate / 100m,
                Total = taxable + taxable * gstRate / 100m,
                Currency = GetCurrency(),
                BillingName = request.BillingName,
                BillingEmail = request.BillingEmail,
                BillingGstin = request.BillingGstin,
                BillingAddress = request.BillingAddress
            };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync(ct);

            var result = await _gateway.CreatePaymentAsync(new PaymentRequest(
                Reference: $"{subscription.Id}:{invoice.InvoiceNumber}",
                Amount: invoice.Total,
                Currency: invoice.Currency,
                Description: $"Subscription - {plan.Name} ({request.BillingCycle})",
                GatewayOrderId: null), ct);

            payment = new Payment
            {
                TenantId = tenantId,
                InvoiceId = invoice.Id,
                Gateway = _gateway.Name,
                GatewayPaymentId = result.PaymentId,
                GatewayOrderId = result.OrderId,
                Amount = invoice.Total,
                Currency = invoice.Currency,
                Method = request.PaymentMethodId,
                Status = result.Success ? PaymentStatus.Succeeded : PaymentStatus.Failed,
                FailureReason = result.Success ? null : result.Error,
                PaidAt = result.Success ? DateTime.UtcNow : null
            };
            db.Payments.Add(payment);

            if (result.Success)
            {
                invoice.Status = InvoiceStatus.Paid;
                invoice.RazorpayOrderId = result.OrderId;
                invoice.PaymentMethod = payment.Method;
            }
            else
            {
                subscription.Status = SubscriptionStatus.PastDue;
            }

            await db.SaveChangesAsync(ct);
        }

        if (promo is not null)
        {
            promo.RedemptionCount++;
        }

        if (coupon is not null)
        {
            coupon.RedemptionCount++;
        }

        await db.SaveChangesAsync(ct);

        var recipient = await ResolveBillingRecipientAsync(db, actorUserId, request.BillingEmail, request.BillingName, ct);
        if (recipient is not null && invoice is not null)
        {
            await _email.SendInvoiceAsync(recipient, invoice, ct);
        }

        _logger.LogInformation(
            "Tenant {TenantId} subscribed to {PlanCode} ({Cycle}) via {Gateway}",
            tenantId, plan.Code, request.BillingCycle, _gateway.Name);

        return new SubscriptionResult(true, subscription, invoice, payment, null);
    }

    public async Task<SubscriptionResult> ChangePlanAsync(int tenantId, string planCode, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var plan = await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Code == planCode && p.IsActive, ct);
        if (plan is null)
        {
            return new SubscriptionResult(false, null, null, null, $"Plan '{planCode}' is not available.");
        }

        var sub = await db.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.TenantId == tenantId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (sub is null)
        {
            return new SubscriptionResult(false, null, null, null, "No active subscription to change.");
        }

        if (sub.PlanId == plan.Id)
        {
            return new SubscriptionResult(true, sub, null, null, null);
        }

        var oldPrice = sub.BillingCycle == BillingCycle.Yearly ? sub.Plan.PricePerYear : sub.Plan.PricePerMonth;
        var newPrice = sub.BillingCycle == BillingCycle.Yearly ? plan.PricePerYear : plan.PricePerMonth;

        sub.PlanId = plan.Id;
        sub.CreditsRemaining = plan.MonthlyCreditAllowance;
        sub.UpdatedAt = DateTime.UtcNow;

        Invoice? invoice = null;
        if (newPrice > oldPrice)
        {
            var difference = newPrice - oldPrice;
            var gstRate = GetGstRate();
            invoice = new Invoice
            {
                TenantId = tenantId,
                SubscriptionId = sub.Id,
                InvoiceNumber = $"INV-{DateTime.UtcNow:yyyyMMdd}-{sub.Id:D4}",
                IssueDate = DateTime.UtcNow,
                DueDate = DateTime.UtcNow.AddDays(7),
                Subtotal = difference,
                Discount = 0,
                TaxRate = gstRate,
                TaxAmount = difference * gstRate / 100m,
                Total = difference + difference * gstRate / 100m,
                Currency = GetCurrency()
            };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync(ct);

            var paymentResult = await _gateway.CreatePaymentAsync(new PaymentRequest(
                Reference: $"{sub.Id}:{invoice.InvoiceNumber}",
                Amount: invoice.Total,
                Currency: invoice.Currency,
                Description: $"Plan change - {plan.Name}"), ct);

            var payment = new Payment
            {
                TenantId = tenantId,
                InvoiceId = invoice.Id,
                Gateway = _gateway.Name,
                GatewayPaymentId = paymentResult.PaymentId,
                GatewayOrderId = paymentResult.OrderId,
                Amount = invoice.Total,
                Currency = invoice.Currency,
                Status = paymentResult.Success ? PaymentStatus.Succeeded : PaymentStatus.Failed,
                FailureReason = paymentResult.Success ? null : paymentResult.Error,
                PaidAt = paymentResult.Success ? DateTime.UtcNow : null
            };
            db.Payments.Add(payment);

            if (paymentResult.Success)
            {
                invoice.Status = InvoiceStatus.Paid;
                invoice.RazorpayOrderId = paymentResult.OrderId;
            }
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Tenant {TenantId} changed plan to {PlanCode}", tenantId, plan.Code);
        return new SubscriptionResult(true, sub, invoice, null, null);
    }

    public async Task<SubscriptionResult> CancelAsync(int tenantId, bool immediate = false, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var sub = await db.Subscriptions
            .Where(s => s.TenantId == tenantId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (sub is null)
        {
            return new SubscriptionResult(false, null, null, null, "No active subscription to cancel.");
        }

        if (immediate)
        {
            sub.Status = SubscriptionStatus.Cancelled;
            sub.CancelledAt = DateTime.UtcNow;
        }
        else
        {
            sub.CancelAtPeriodEnd = sub.CurrentPeriodEnd ?? DateTime.UtcNow.AddMonths(1);
        }

        sub.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new SubscriptionResult(true, sub, null, null, null);
    }

    public async Task<SubscriptionResult> RolloverCycleAsync(int tenantId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var sub = await db.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.TenantId == tenantId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (sub is null)
        {
            return new SubscriptionResult(false, null, null, null, "No active subscription.");
        }

        sub.CurrentPeriodStart = sub.CurrentPeriodEnd ?? DateTime.UtcNow;
        sub.CurrentPeriodEnd = sub.BillingCycle == BillingCycle.Yearly
            ? sub.CurrentPeriodStart.Value.AddYears(1)
            : sub.CurrentPeriodStart.Value.AddMonths(1);
        sub.CreditsRemaining = sub.Plan?.MonthlyCreditAllowance ?? sub.CreditsRemaining;
        sub.CancelAtPeriodEnd = null;
        sub.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return new SubscriptionResult(true, sub, null, null, null);
    }

    public async Task<List<UsageHistory>> GetUsageAsync(int tenantId, int take = 50, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.UsageHistory.AsNoTracking()
            .Where(u => u.TenantId == tenantId)
            .OrderByDescending(u => u.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    private static Subscription CreateSubscription(
        int tenantId,
        SubscriptionPlan plan,
        BillingCycle cycle,
        PromoCode? promo,
        Coupon? coupon,
        decimal? discount = null,
        int couponBonusCredits = 0)
    {
        var now = DateTime.UtcNow;
        return new Subscription
        {
            TenantId = tenantId,
            PlanId = plan.Id,
            Status = SubscriptionStatus.Active,
            BillingCycle = cycle,
            StartDate = now,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = cycle == BillingCycle.Yearly ? now.AddYears(1) : now.AddMonths(1),
            CreditsRemaining = plan.MonthlyCreditAllowance + couponBonusCredits,
            PromoCodeId = promo?.Id,
            CouponId = coupon?.Id,
            DiscountAmount = discount
        };
    }

    private static decimal ComputeDiscount(
        SubscriptionPlan plan,
        decimal basePrice,
        BillingCycle cycle,
        PromoCode? promo)
    {
        if (promo is null)
        {
            return 0m;
        }

        return promo.Type switch
        {
            PromoType.PercentOff => basePrice * promo.Value / 100m,
            PromoType.FixedOff => Math.Min(promo.Value, basePrice),
            PromoType.FreeMonths when cycle == BillingCycle.Monthly =>
                basePrice * Math.Min(promo.FreeMonths, 11) / 12m,
            PromoType.FreeMonths =>
                basePrice * Math.Min(promo.FreeMonths, 12) / 12m,
            _ => 0m
        };
    }

    private static async Task<PromoCode?> ResolvePromoAsync(
        DailyPosterDbContext db,
        string? code,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var promo = await db.PromoCodes.FirstOrDefaultAsync(p => p.Code == code && p.IsActive, ct);
        if (promo is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (promo.ValidFrom is not null && promo.ValidFrom > now)
        {
            return null;
        }

        if (promo.ValidUntil is not null && promo.ValidUntil < now)
        {
            return null;
        }

        if (promo.MaxRedemptions is not null && promo.RedemptionCount >= promo.MaxRedemptions)
        {
            return null;
        }

        return promo;
    }

    private static async Task<Coupon?> ResolveCouponAsync(
        DailyPosterDbContext db,
        string? code,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var coupon = await db.Coupons.FirstOrDefaultAsync(c => c.Code == code && c.IsActive, ct);
        if (coupon is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (coupon.ValidFrom is not null && coupon.ValidFrom > now)
        {
            return null;
        }

        if (coupon.ValidUntil is not null && coupon.ValidUntil < now)
        {
            return null;
        }

        if (coupon.MaxRedemptions is not null && coupon.RedemptionCount >= coupon.MaxRedemptions)
        {
            return null;
        }

        return coupon;
    }

    private static async Task<AppUser?> ResolveBillingRecipientAsync(
        DailyPosterDbContext db,
        int? actorUserId,
        string? billingEmail,
        string? billingName,
        CancellationToken ct)
    {
        if (actorUserId is not null)
        {
            var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == actorUserId, ct);
            if (user is not null)
            {
                return user;
            }
        }

        if (!string.IsNullOrWhiteSpace(billingEmail))
        {
            return new AppUser
            {
                Email = billingEmail,
                DisplayName = billingName ?? billingEmail
            };
        }

        return null;
    }

    private decimal GetGstRate() => _config.GetValue<decimal>("Billing:GstRate", 18m);
    private string GetCurrency() => _config["Billing:Currency"] ?? "INR";
}
