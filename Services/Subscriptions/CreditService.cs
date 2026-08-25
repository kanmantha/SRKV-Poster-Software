using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Services.Subscriptions;

public class CreditCostOptions
{
    public int Poster { get; set; } = 1;
    public int AiImage { get; set; } = 5;
    public int BackgroundRemoval { get; set; } = 2;
    public int Upscale { get; set; } = 3;
    public int Rewrite { get; set; } = 1;
}

public record CreditBalance(int Available, int Allowance);

public record SpendResult(bool Success, int CreditsSpent, int Remaining, string? Error);

public interface ICreditService
{
    int CostOf(string feature);

    Task<CreditBalance> GetBalanceAsync(int tenantId, CancellationToken ct = default);

    Task<SpendResult> SpendAsync(int tenantId, string feature, int? userId = null, string? description = null, CancellationToken ct = default);

    Task<SpendResult> SpendCreditsAsync(int tenantId, string feature, int credits, int? userId = null, string? description = null, CancellationToken ct = default);
}

public class CreditService : ICreditService
{
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly CreditCostOptions _costs;

    public CreditService(IDbContextFactory<DailyPosterDbContext> dbFactory, IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _costs = configuration.GetSection("CreditCosts").Get<CreditCostOptions>() ?? new CreditCostOptions();
    }

    public int CostOf(string feature) => feature switch
    {
        "poster" => _costs.Poster,
        "ai-image" => _costs.AiImage,
        "bg-removal" => _costs.BackgroundRemoval,
        "upscale" => _costs.Upscale,
        "rewrite" => _costs.Rewrite,
        _ => 1
    };

    public async Task<CreditBalance> GetBalanceAsync(int tenantId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var sub = await db.Subscriptions.AsNoTracking()
            .Include(s => s.Plan)
            .Where(s => s.TenantId == tenantId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return sub is null
            ? new CreditBalance(0, 0)
            : new CreditBalance(sub.CreditsRemaining, sub.Plan?.MonthlyCreditAllowance ?? 0);
    }

    public Task<SpendResult> SpendAsync(
        int tenantId,
        string feature,
        int? userId = null,
        string? description = null,
        CancellationToken ct = default) =>
        SpendCreditsAsync(tenantId, feature, CostOf(feature), userId, description, ct);

    public async Task<SpendResult> SpendCreditsAsync(
        int tenantId,
        string feature,
        int credits,
        int? userId = null,
        string? description = null,
        CancellationToken ct = default)
    {
        if (credits <= 0)
        {
            return new SpendResult(true, 0, 0, null);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var sub = await db.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.TenantId == tenantId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (sub is null)
        {
            return new SpendResult(false, 0, 0, "No active subscription for this tenant.");
        }

        if (sub.CreditsRemaining < credits)
        {
            return new SpendResult(false, 0, sub.CreditsRemaining,
                $"Insufficient credits. This operation needs {credits}, available: {sub.CreditsRemaining}.");
        }

        sub.CreditsRemaining -= credits;
        db.UsageHistory.Add(new UsageHistory
        {
            TenantId = tenantId,
            UserId = userId,
            Feature = feature,
            CreditsSpent = credits,
            Description = description
        });

        await db.SaveChangesAsync(ct);
        return new SpendResult(true, credits, sub.CreditsRemaining, null);
    }
}
