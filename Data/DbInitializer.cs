using DailyPosterGenerator.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(DailyPosterDbContext db, IConfiguration config, bool useSqlite = false)
    {
        if (useSqlite)
        {
            await db.Database.EnsureCreatedAsync();
        }
        else
        {
            await db.Database.MigrateAsync();
        }

        await SeedPlatformsAsync(db);
        await SeedDefaultTenantAsync(db);
        await SeedDefaultSettingsAsync(db, config);
        await SeedSubscriptionPlansAsync(db);
        await SeedPromoCodesAsync(db);
        await SeedSystemTemplatesAsync(db);
        await SeedAdminUserAsync(db, config);
    }

    private static async Task SeedPlatformsAsync(DailyPosterDbContext db)
    {
        if (await db.Platforms.AnyAsync())
        {
            return;
        }

        var platforms = new[]
        {
            new Platform { Name = "Twitter / X", Enabled = false, AccountHandle = "@daily_poster" },
            new Platform { Name = "Facebook", Enabled = false, AccountHandle = "Daily Poster" },
            new Platform { Name = "Instagram", Enabled = false, AccountHandle = "@daily.poster" },
            new Platform { Name = "LinkedIn", Enabled = false, AccountHandle = "Daily Poster" },
            new Platform { Name = "Threads", Enabled = false, AccountHandle = "@daily.poster" }
        };

        db.Platforms.AddRange(platforms);
        await db.SaveChangesAsync();
    }

    private static async Task SeedDefaultTenantAsync(DailyPosterDbContext db)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync();
        if (tenant is null)
        {
            db.Tenants.Add(new Tenant
            {
                Name = "Sri Ramakrishna Vidyapeetham",
                Slug = "srkv",
                Sector = SectorCatalog.Education,
                IsActive = true
            });
            await db.SaveChangesAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(tenant.Sector))
        {
            tenant.Sector = SectorCatalog.Education;
            await db.SaveChangesAsync();
        }
    }

    private static async Task SeedDefaultSettingsAsync(DailyPosterDbContext db, IConfiguration config)
    {
        const int tenantId = 1;
        var defaults = new (string Key, string? Value)[]
        {
            ("ai.enabled", config.GetValue("AppSettings:ai:enabled", true).ToString()),
            ("ai.endpoint", config["AppSettings:ai:endpoint"] ?? "https://api.openai.com/v1"),
            ("ai.apiKey", config["AppSettings:ai:apiKey"] ?? string.Empty),
            ("ai.chatModel", config["AppSettings:ai:chatModel"] ?? "gpt-4o-mini"),
            ("ai.imageModel", config["AppSettings:ai:imageModel"] ?? "dall-e-3"),
            ("ai.generateImages", config.GetValue("AppSettings:ai:generateImages", false).ToString()),
            ("ai.timeoutSeconds", config.GetValue("AppSettings:ai:timeoutSeconds", 90).ToString()),
            ("scheduler.enabled", config.GetValue("AppSettings:scheduler:enabled", true).ToString()),
            ("scheduler.time", config["AppSettings:scheduler:time"] ?? "06:00"),
            ("poster.theme", config["AppSettings:poster:theme"] ?? "srv"),
            ("school.name", config["AppSettings:school:name"] ?? "Sri Ramakrishna Vidyalayam"),
            ("school.city", config["AppSettings:school:city"] ?? "Khammam"),
            ("school.tagline", config["AppSettings:school:tagline"] ?? "Education with Values | Discipline in Life"),
            ("school.showValues", config.GetValue("AppSettings:school:showValues", true).ToString()),
            ("school.values", config["AppSettings:school:values"] ?? "Compassion,Respect,Discipline,Inclusion,Empowerment,Service"),
            ("org.name", config["AppSettings:org:name"] ?? "Your Organization"),
            ("org.city", config["AppSettings:org:city"] ?? string.Empty),
            ("org.tagline", config["AppSettings:org:tagline"] ?? string.Empty),
            ("org.showValues", config.GetValue("AppSettings:org:showValues", true).ToString()),
            ("org.values", config["AppSettings:org:values"] ?? "Quality,Service,Trust,Community,Integrity,Excellence"),
            ("org.facebook", config["AppSettings:org:facebook"] ?? string.Empty),
            ("org.instagram", config["AppSettings:org:instagram"] ?? string.Empty),
            ("org.phones", config["AppSettings:org:phones"] ?? string.Empty)
        };

        foreach (var (key, value) in defaults)
        {
            var existing = await db.SystemSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Key == key);
            if (existing is null)
            {
                db.SystemSettings.Add(new SystemSetting { TenantId = tenantId, Key = key, Value = value });
            }
            else if (key.StartsWith("ai."))
            {
                var envValue = Environment.GetEnvironmentVariable("AppSettings__" + key.Replace(".", "__"));
                if (!string.IsNullOrWhiteSpace(envValue))
                {
                    existing.Value = envValue;
                }
            }
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedSubscriptionPlansAsync(DailyPosterDbContext db)
    {
        if (await db.SubscriptionPlans.AnyAsync())
        {
            return;
        }

        var plans = new[]
        {
            new SubscriptionPlan
            {
                Code = "FREE", Name = "Free", SortOrder = 0, IsDefault = true,
                Description = "Get started with basic daily posters.",
                PricePerMonth = 0, PricePerYear = 0, MonthlyCreditAllowance = 5, MaxUsers = 1,
                AllowsAiGeneration = true, AllowsExport = true, AllowsPublishing = false,
                AllowsAiImageGeneration = false, AllowsBackgroundRemoval = false, AllowsUpscale = false,
                AllowsContentRewrite = false, AllowsCustomBranding = false, AllowsPrioritySupport = false
            },
            new SubscriptionPlan
            {
                Code = "STARTER", Name = "Starter", SortOrder = 1,
                Description = "For growing schools. 20 credits/month.",
                PricePerMonth = 499, PricePerYear = 4990, MonthlyCreditAllowance = 20, MaxUsers = 3,
                AllowsAiGeneration = true, AllowsAiImageGeneration = true, AllowsExport = true, AllowsPublishing = true,
                AllowsBackgroundRemoval = false, AllowsUpscale = false, AllowsContentRewrite = true,
                AllowsCustomBranding = false, AllowsPrioritySupport = false
            },
            new SubscriptionPlan
            {
                Code = "PRO", Name = "Pro", SortOrder = 2,
                Description = "Everything for serious publishers. 80 credits/month.",
                PricePerMonth = 1499, PricePerYear = 14990, MonthlyCreditAllowance = 80, MaxUsers = 10,
                AllowsAiGeneration = true, AllowsAiImageGeneration = true, AllowsBackgroundRemoval = true,
                AllowsUpscale = true, AllowsContentRewrite = true, AllowsCustomBranding = true,
                AllowsExport = true, AllowsPublishing = true, AllowsPrioritySupport = false
            },
            new SubscriptionPlan
            {
                Code = "BUSINESS", Name = "Business", SortOrder = 3,
                Description = "Unlimited power for chains and agencies.",
                PricePerMonth = 4999, PricePerYear = 49990, MonthlyCreditAllowance = 300, MaxUsers = 50,
                AllowsAiGeneration = true, AllowsAiImageGeneration = true, AllowsBackgroundRemoval = true,
                AllowsUpscale = true, AllowsContentRewrite = true, AllowsCustomBranding = true,
                AllowsExport = true, AllowsPublishing = true, AllowsPrioritySupport = true
            }
        };

        db.SubscriptionPlans.AddRange(plans);
        await db.SaveChangesAsync();
    }

    private static async Task SeedPromoCodesAsync(DailyPosterDbContext db)
    {
        if (await db.PromoCodes.AnyAsync() || await db.Coupons.AnyAsync())
        {
            return;
        }

        db.PromoCodes.AddRange(
            new PromoCode { Code = "LAUNCH20", Type = PromoType.PercentOff, Value = 20, MaxRedemptions = 500, IsActive = true },
            new PromoCode { Code = "WELCOME10", Type = PromoType.PercentOff, Value = 10, MaxRedemptions = 1000, IsActive = true }
        );

        db.Coupons.Add(
            new Coupon { Code = "FIRST50", Type = CouponType.FixedAmount, Value = 50, MaxRedemptions = 250, IsActive = true }
        );

        await db.SaveChangesAsync();
    }

    private static async Task SeedSystemTemplatesAsync(DailyPosterDbContext db)
    {
        const int systemTenantId = 0;
        var now = DateTime.UtcNow;
        var existing = await db.PosterTemplates
            .Where(t => t.TenantId == systemTenantId)
            .ToListAsync();

        var desired = new[]
        {
            new { Name = "SRV School Classic", Sector = SectorCatalog.Education,
                  Desc = "Default school poster with the SRV crest.", Theme = "srv", Accent = (string?)null },
            new { Name = "Vibrant Celebration", Sector = SectorCatalog.General,
                  Desc = "Colorful festive style for celebrations and special days.", Theme = "colorful", Accent = (string?)"#FF6600" },
            new { Name = "Minimal Light", Sector = SectorCatalog.General,
                  Desc = "Clean, light, modern design.", Theme = "light", Accent = (string?)"#1E5B9A" },
            new { Name = "Midnight", Sector = SectorCatalog.General,
                  Desc = "Dark elegant theme for evenings and formal events.", Theme = "dark", Accent = (string?)"#F5C518" },
            new { Name = "Auto Theme", Sector = SectorCatalog.General,
                  Desc = "Adapts to the event and time of day.", Theme = "auto", Accent = (string?)null },
            new { Name = "Menu of the Day", Sector = SectorCatalog.Restaurant,
                  Desc = "Appetising layout for today's menu and specials.", Theme = "light", Accent = (string?)"#C0392B" },
            new { Name = "Election Day Alert", Sector = SectorCatalog.Politics,
                  Desc = "Bold campaign style for rallies and polling days.", Theme = "dark", Accent = (string?)"#2E86DE" },
            new { Name = "Game Day", Sector = SectorCatalog.Sports,
                  Desc = "Energetic layout for matches, fixtures and results.", Theme = "colorful", Accent = (string?)"#27AE60" },
            new { Name = "Daily Offer", Sector = SectorCatalog.Retail,
                  Desc = "Simple, punchy layout for deals and new stock.", Theme = "light", Accent = (string?)"#8E44AD" }
        };

        var changed = false;
        foreach (var t in desired)
        {
            var entity = existing.FirstOrDefault(e => e.Name == t.Name);
            if (entity is null)
            {
                db.PosterTemplates.Add(new PosterTemplate
                {
                    TenantId = systemTenantId,
                    Name = t.Name,
                    Description = t.Desc,
                    Sector = t.Sector,
                    Theme = t.Theme,
                    AccentColor = t.Accent,
                    IsSystem = true,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                changed = true;
            }
            else if (entity.Sector != t.Sector || entity.Theme != t.Theme || entity.AccentColor != t.Accent)
            {
                entity.Sector = t.Sector;
                entity.Theme = t.Theme;
                entity.AccentColor = t.Accent;
                entity.Description = t.Desc;
                entity.UpdatedAt = now;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
    }

    private static async Task SeedAdminUserAsync(DailyPosterDbContext db, IConfiguration config)
    {
        var adminEmail = (config["AdminEmail"] ?? config["SaaS:AdminEmail"] ?? "admin@srkv.ac.in").Trim().ToLowerInvariant();
        if (await db.AppUsers.AnyAsync(u => u.Email == adminEmail))
        {
            return;
        }

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == 1);
        if (tenant is null)
        {
            return;
        }

        var user = new AppUser
        {
            TenantId = 1,
            Email = adminEmail,
            DisplayName = "Administrator",
            EmailConfirmed = true,
            IsAdmin = true
        };

        var password = config["AdminPassword"] ?? config["SaaS:AdminPassword"] ?? "Admin@12345";
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);

        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
    }
}
