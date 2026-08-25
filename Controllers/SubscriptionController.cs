using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services.MultiTenancy;
using DailyPosterGenerator.Services.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DailyPosterGenerator.Controllers;

[Authorize]
public class SubscriptionController : Controller
{
    private readonly ISubscriptionService _subscriptions;
    private readonly ICreditService _credits;
    private readonly TenantContext _tenant;

    public SubscriptionController(ISubscriptionService subscriptions, ICreditService credits, TenantContext tenant)
    {
        _subscriptions = subscriptions;
        _credits = credits;
        _tenant = tenant;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var plans = await _subscriptions.GetPlansAsync(ct);
        var subscription = await _subscriptions.GetLatestAsync(_tenant.TenantId, ct);
        var balance = subscription is not null
            ? await _credits.GetBalanceAsync(_tenant.TenantId, ct)
            : new CreditBalance(0, 0);

        return View(new SubscriptionIndexViewModel
        {
            Plans = plans,
            Subscription = subscription,
            CreditsAvailable = balance.Available,
            CreditsAllowance = balance.Allowance
        });
    }
}
