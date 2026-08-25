using DailyPosterGenerator.Services.MultiTenancy;
using DailyPosterGenerator.Services.Subscriptions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DailyPosterGenerator.Controllers.Api;

[Route("api/subscriptions")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class SubscriptionController : ControllerBase
{
    private readonly ISubscriptionService _subscriptions;
    private readonly ICreditService _credits;
    private readonly TenantContext _tenant;

    public SubscriptionController(
        ISubscriptionService subscriptions,
        ICreditService credits,
        TenantContext tenant)
    {
        _subscriptions = subscriptions;
        _credits = credits;
        _tenant = tenant;
    }

    [HttpGet("plans")]
    [AllowAnonymous]
    public async Task<IActionResult> Plans(CancellationToken ct)
    {
        var plans = await _subscriptions.GetPlansAsync(ct);
        return Ok(plans.Select(p => new
        {
            p.Code,
            p.Name,
            p.Description,
            p.PricePerMonth,
            p.PricePerYear,
            p.Currency,
            p.MonthlyCreditAllowance,
            p.MaxUsers,
            Features = new
            {
                AiText = p.AllowsAiGeneration,
                AiImage = p.AllowsAiImageGeneration,
                BackgroundRemoval = p.AllowsBackgroundRemoval,
                Upscale = p.AllowsUpscale,
                Rewrite = p.AllowsContentRewrite,
                Branding = p.AllowsCustomBranding,
                Export = p.AllowsExport,
                Publish = p.AllowsPublishing,
                PrioritySupport = p.AllowsPrioritySupport
            }
        }));
    }

    [HttpGet("current")]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var subscription = await _subscriptions.GetLatestAsync(_tenant.TenantId, ct);
        if (subscription is null)
        {
            return NotFound(new { error = "No subscription found for this tenant." });
        }

        var balance = await _credits.GetBalanceAsync(_tenant.TenantId, ct);
        return Ok(new
        {
            subscription.Id,
            Plan = subscription.Plan?.Code,
            subscription.BillingCycle,
            subscription.Status,
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            subscription.CancelAtPeriodEnd,
            subscription.DiscountAmount,
            Credits = new { balance.Available, balance.Allowance }
        });
    }

    [HttpPost]
    public async Task<IActionResult> Subscribe(SubscribeRequest request, CancellationToken ct)
    {
        var result = await _subscriptions.SubscribeAsync(
            _tenant.TenantId, request, _tenant.UserId, ct);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(new
        {
            result.Success,
            SubscriptionId = result.Subscription?.Id,
            InvoiceNumber = result.Invoice?.InvoiceNumber,
            Total = result.Invoice?.Total,
            PaymentId = result.Payment?.GatewayPaymentId,
            PaymentStatus = result.Payment?.Status.ToString()
        });
    }

    [HttpPost("change-plan")]
    public async Task<IActionResult> ChangePlan([FromBody] ChangePlanRequest request, CancellationToken ct)
    {
        var result = await _subscriptions.ChangePlanAsync(_tenant.TenantId, request.PlanCode, ct);
        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(new { result.Success, PlanCode = result.Subscription?.Plan?.Code });
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel([FromBody] CancelRequest request, CancellationToken ct)
    {
        var result = await _subscriptions.CancelAsync(_tenant.TenantId, request.Immediate, ct);
        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(new { result.Success, result.Subscription?.Status, result.Subscription?.CancelAtPeriodEnd });
    }

    [HttpGet("usage")]
    public async Task<IActionResult> Usage(CancellationToken ct)
    {
        var usage = await _subscriptions.GetUsageAsync(_tenant.TenantId, 50, ct);
        return Ok(usage.Select(u => new
        {
            u.Feature,
            u.CreditsSpent,
            u.Description,
            u.CreatedAt
        }));
    }

    public class ChangePlanRequest
    {
        public string PlanCode { get; set; } = string.Empty;
    }

    public class CancelRequest
    {
        public bool Immediate { get; set; }
    }
}
