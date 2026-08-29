using DailyPosterGenerator.Models;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Data;

public class DailyPosterDbContext : DbContext
{
    public DailyPosterDbContext(DbContextOptions<DailyPosterDbContext> options)
        : base(options)
    {
    }

    public DbSet<Poster> Posters => Set<Poster>();
    public DbSet<PosterTemplate> PosterTemplates => Set<PosterTemplate>();
    public DbSet<PosterEvent> PosterEvents => Set<PosterEvent>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<Platform> Platforms => Set<Platform>();

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<PromoCode> PromoCodes => Set<PromoCode>();
    public DbSet<UsageHistory> UsageHistory => Set<UsageHistory>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (Database.IsNpgsql())
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties()
                    .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)))
                {
                    property.SetColumnType("timestamp without time zone");
                }
            }
        }

        modelBuilder.Entity<PosterEvent>()
            .HasOne(e => e.Poster)
            .WithMany(p => p.Events)
            .HasForeignKey(e => e.PosterId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Poster>()
            .HasIndex(p => new { p.EventDate })
            .HasDatabaseName("IX_Poster_EventDate");

        modelBuilder.Entity<Platform>()
            .HasIndex(p => p.Name)
            .IsUnique();

        // ---- Templates ----
        modelBuilder.Entity<PosterTemplate>()
            .HasIndex(t => new { t.TenantId, t.Name })
            .IsUnique();

        modelBuilder.Entity<Poster>()
            .HasOne(p => p.Template)
            .WithMany()
            .HasForeignKey(p => p.TemplateId)
            .OnDelete(DeleteBehavior.Restrict);

        // ---- Multi-tenancy ----
        modelBuilder.Entity<Tenant>()
            .HasIndex(t => t.Slug)
            .IsUnique();

        modelBuilder.Entity<SystemSetting>()
            .HasIndex(s => new { s.TenantId, s.Key })
            .IsUnique();

        modelBuilder.Entity<Poster>()
            .HasOne(p => p.Tenant)
            .WithMany(t => t.Posters)
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PosterEvent>()
            .HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SystemSetting>()
            .HasOne(s => s.Tenant)
            .WithMany(t => t.Settings)
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // ---- SaaS accounts ----
        modelBuilder.Entity<AppUser>()
            .HasIndex(u => new { u.TenantId, u.Email })
            .IsUnique();

        modelBuilder.Entity<AppUser>()
            .HasOne(u => u.Tenant)
            .WithMany(t => t.Users)
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RefreshToken>()
            .HasOne(r => r.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // ---- Billing ----
        modelBuilder.Entity<SubscriptionPlan>()
            .HasIndex(p => p.Code)
            .IsUnique();

        modelBuilder.Entity<Subscription>()
            .HasOne(s => s.Tenant)
            .WithMany()
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Subscription>()
            .HasOne(s => s.Plan)
            .WithMany()
            .HasForeignKey(s => s.PlanId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Subscription>()
            .HasOne(s => s.PromoCode)
            .WithMany(p => p.Subscriptions)
            .HasForeignKey(s => s.PromoCodeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Subscription>()
            .HasOne(s => s.Coupon)
            .WithMany(c => c.Subscriptions)
            .HasForeignKey(s => s.CouponId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Invoice>()
            .HasOne(i => i.Tenant)
            .WithMany()
            .HasForeignKey(i => i.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Invoice>()
            .HasOne(i => i.Subscription)
            .WithMany(s => s.Invoices)
            .HasForeignKey(i => i.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Invoice>()
            .HasOne(i => i.Coupon)
            .WithMany(c => c.Invoices)
            .HasForeignKey(i => i.CouponId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payment>()
            .HasOne(p => p.Tenant)
            .WithMany()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payment>()
            .HasOne(p => p.Invoice)
            .WithMany()
            .HasForeignKey(p => p.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Coupon>()
            .HasIndex(c => c.Code)
            .IsUnique();

        modelBuilder.Entity<PromoCode>()
            .HasIndex(p => p.Code)
            .IsUnique();

        // Money/percent precision.
        const int moneyPrecision = 18;
        const int moneyScale = 2;

        modelBuilder.Entity<SubscriptionPlan>(e =>
        {
            e.Property(p => p.PricePerMonth).HasPrecision(moneyPrecision, moneyScale);
            e.Property(p => p.PricePerYear).HasPrecision(moneyPrecision, moneyScale);
        });

        modelBuilder.Entity<Subscription>(e =>
            e.Property(s => s.DiscountAmount).HasPrecision(moneyPrecision, moneyScale));

        modelBuilder.Entity<Invoice>(e =>
        {
            e.Property(i => i.Subtotal).HasPrecision(moneyPrecision, moneyScale);
            e.Property(i => i.Discount).HasPrecision(moneyPrecision, moneyScale);
            e.Property(i => i.TaxRate).HasPrecision(moneyPrecision, 2);
            e.Property(i => i.TaxAmount).HasPrecision(moneyPrecision, moneyScale);
            e.Property(i => i.Total).HasPrecision(moneyPrecision, moneyScale);
        });

        modelBuilder.Entity<Payment>(e =>
            e.Property(p => p.Amount).HasPrecision(moneyPrecision, moneyScale));

        modelBuilder.Entity<Coupon>(e =>
            e.Property(c => c.Value).HasPrecision(moneyPrecision, moneyScale));

        modelBuilder.Entity<PromoCode>(e =>
            e.Property(p => p.Value).HasPrecision(moneyPrecision, moneyScale));

        modelBuilder.Entity<UsageHistory>()
            .HasOne(u => u.Tenant)
            .WithMany()
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<UsageHistory>()
            .HasOne(u => u.User)
            .WithMany(a => a.Usage)
            .HasForeignKey(u => u.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
