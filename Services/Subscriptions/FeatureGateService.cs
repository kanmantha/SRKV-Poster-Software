using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Services.Subscriptions;

public static class FeatureCatalog
{
    public const string AiText = "ai";
    public const string AiImage = "ai-image";
    public const string BackgroundRemoval = "bg-removal";
    public const string Upscale = "upscale";
    public const string Rewrite = "rewrite";
    public const string Branding = "branding";
    public const string Export = "export";
    public const string Publish = "publish";
    public const string PrioritySupport = "priority-support";
}

public interface IFeatureGateService
{
    Task<SubscriptionPlan?> GetEffectivePlanAsync(int tenantId, CancellationToken ct = default);

    Task<bool> HasFeatureAsync(int tenantId, string feature, CancellationToken ct = default);
}

public class FeatureGateService : IFeatureGateService
{
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;

    public FeatureGateService(IDbContextFactory<DailyPosterDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<SubscriptionPlan?> GetEffectivePlanAsync(int tenantId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var activePlan = await db.Subscriptions.AsNoTracking()
            .Include(s => s.Plan)
            .Where(s => s.TenantId == tenantId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.Plan)
            .FirstOrDefaultAsync(ct);

        if (activePlan is not null)
        {
            return activePlan;
        }

        return await db.SubscriptionPlans.AsNoTracking()
            .FirstOrDefaultAsync(p => p.IsDefault && p.IsActive, ct);
    }

    public async Task<bool> HasFeatureAsync(int tenantId, string feature, CancellationToken ct = default)
    {
        var plan = await GetEffectivePlanAsync(tenantId, ct);
        return plan is not null && PlanHasFeature(plan, feature);
    }

    public static bool PlanHasFeature(SubscriptionPlan plan, string feature) => feature switch
    {
        FeatureCatalog.AiText => plan.AllowsAiGeneration,
        FeatureCatalog.AiImage => plan.AllowsAiImageGeneration,
        FeatureCatalog.BackgroundRemoval => plan.AllowsBackgroundRemoval,
        FeatureCatalog.Upscale => plan.AllowsUpscale,
        FeatureCatalog.Rewrite => plan.AllowsContentRewrite,
        FeatureCatalog.Branding => plan.AllowsCustomBranding,
        FeatureCatalog.Export => plan.AllowsExport,
        FeatureCatalog.Publish => plan.AllowsPublishing,
        FeatureCatalog.PrioritySupport => plan.AllowsPrioritySupport,
        _ => true
    };
}
