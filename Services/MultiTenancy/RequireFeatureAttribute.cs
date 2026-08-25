using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services.Subscriptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DailyPosterGenerator.Services.MultiTenancy;

/// <summary>
/// Guards an action with a plan feature check. Returns 403 when the tenant's plan
/// does not include the requested feature.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireFeatureAttribute : Attribute, IAsyncActionFilter
{
    private readonly string _feature;

    public RequireFeatureAttribute(string feature)
    {
        _feature = feature;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenantContext = context.HttpContext.RequestServices.GetRequiredService<TenantContext>();
        if (!tenantContext.IsAuthenticated)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var gates = context.HttpContext.RequestServices.GetRequiredService<IFeatureGateService>();
        if (!await gates.HasFeatureAsync(tenantContext.TenantId, _feature))
        {
            context.Result = new ObjectResult(new
            {
                error = $"Your current plan does not include the '{_feature}' feature."
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }
}

/// <summary>
/// Guards an action with an active-subscription check. Returns 402 when the
/// tenant has no active (paid or free) subscription.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireSubscriptionAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenantContext = context.HttpContext.RequestServices.GetRequiredService<TenantContext>();
        if (!tenantContext.IsAuthenticated)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        if (tenantContext.SubscriptionStatus != SubscriptionStatus.Active)
        {
            context.Result = new ObjectResult(new
            {
                error = "An active subscription is required for this operation."
            })
            {
                StatusCode = StatusCodes.Status402PaymentRequired
            };
            return;
        }

        await next();
    }
}
