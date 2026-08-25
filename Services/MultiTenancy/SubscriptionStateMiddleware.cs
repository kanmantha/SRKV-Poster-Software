using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Services.MultiTenancy;

/// <summary>
/// Loads the authenticated tenant's subscription snapshot into TenantContext so
/// per-request validation can run without a further database hit. Runs after
/// authentication and tenant resolution.
/// </summary>
public class SubscriptionStateMiddleware
{
    private readonly RequestDelegate _next;

    public SubscriptionStateMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        if (tenantContext.IsAuthenticated)
        {
            try
            {
                var factory = context.RequestServices.GetRequiredService<IDbContextFactory<DailyPosterDbContext>>();
                await using var db = await factory.CreateDbContextAsync();

                var subscription = await db.Subscriptions.AsNoTracking()
                    .Include(s => s.Plan)
                    .Where(s => s.TenantId == tenantContext.TenantId)
                    .OrderByDescending(s => s.CreatedAt)
                    .FirstOrDefaultAsync();

                if (subscription is not null)
                {
                    tenantContext.SubscriptionStatus = subscription.Status;
                    tenantContext.PlanCode = subscription.Plan?.Code;
                    tenantContext.CreditsRemaining = subscription.CreditsRemaining;
                    tenantContext.PeriodEnd = subscription.CurrentPeriodEnd;
                    tenantContext.TrialEndsAt = subscription.TrialEndsAt;
                }
            }
            catch
            {
                // A subscription lookup failure must never break the request pipeline.
            }
        }

        await _next(context);
    }
}
