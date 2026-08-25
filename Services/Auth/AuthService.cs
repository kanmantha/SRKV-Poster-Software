using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services.Email;
using DailyPosterGenerator.Services.Subscriptions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Services.Auth;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, string? baseUrl, string? ipAddress, CancellationToken ct = default);

    Task<AuthResponse> LoginAsync(string email, string password, string? ipAddress, CancellationToken ct = default);

    Task<AuthResponse> RefreshAsync(string refreshToken, string? ipAddress, CancellationToken ct = default);

    Task<bool> LogoutAsync(string refreshToken, CancellationToken ct = default);

    Task<AuthResponse> VerifyEmailAsync(string token, CancellationToken ct = default);

    Task<bool> ForgotPasswordAsync(string email, string? baseUrl, CancellationToken ct = default);

    Task<AuthResponse> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken ct = default);

    Task<AuthResponse> ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default);
}

public class AuthService : IAuthService
{
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly IJwtTokenService _tokens;
    private readonly IEmailService _email;
    private readonly ISubscriptionService _subscriptions;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IDbContextFactory<DailyPosterDbContext> dbFactory,
        IPasswordHasher<AppUser> hasher,
        IJwtTokenService tokens,
        IEmailService email,
        ISubscriptionService subscriptions,
        IConfiguration config,
        ILogger<AuthService> logger)
    {
        _dbFactory = dbFactory;
        _hasher = hasher;
        _tokens = tokens;
        _email = email;
        _subscriptions = subscriptions;
        _config = config;
        _logger = logger;
    }

    public async Task<AuthResponse> RegisterAsync(
        RegisterRequest request,
        string? baseUrl,
        string? ipAddress,
        CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (await db.AppUsers.AnyAsync(u => u.Email == email, ct))
        {
            return AuthResponse.Fail("An account with this email already exists.");
        }

        var tenant = new Tenant
        {
            Name = string.IsNullOrWhiteSpace(request.OrganizationName)
                ? $"{request.DisplayName.Trim()}'s Workspace"
                : request.OrganizationName.Trim(),
            Slug = Guid.NewGuid().ToString("N")[..10],
            Sector = SectorCatalog.Normalize(request.Sector),
            IsActive = true
        };

        var emailDeliverable = !string.IsNullOrWhiteSpace(_config["Smtp:Host"]);
        var user = new AppUser
        {
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            // When no SMTP server is configured the verification link cannot be
            // delivered, so the account is confirmed immediately (dev-friendly).
            EmailConfirmed = emailDeliverable ? false : true,
            EmailVerificationToken = emailDeliverable ? _tokens.Hash(Guid.NewGuid().ToString("N")) : null,
            EmailVerificationTokenExpires = emailDeliverable ? DateTime.UtcNow.AddHours(24) : null
        };

        user.PasswordHash = _hasher.HashPassword(user, request.Password);

        tenant.Users.Add(user);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        if (emailDeliverable && !string.IsNullOrWhiteSpace(user.EmailVerificationToken))
        {
            await _email.SendVerificationAsync(user, $"{baseUrl}/verify?token={Uri.EscapeDataString(user.EmailVerificationToken)}");
        }

        await _subscriptions.CreateTrialAsync(tenant.Id, ct);

        _logger.LogInformation("Registered new tenant {TenantId} user {Email}", tenant.Id, user.Email);
        return new AuthResponse { Success = true };
    }

    public async Task<AuthResponse> LoginAsync(
        string email,
        string password,
        string? ipAddress,
        CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var user = await db.AppUsers
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Email == normalized, ct);

        if (user is null)
        {
            return AuthResponse.Fail("Invalid email or password.");
        }

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Success)
        {
            return AuthResponse.Fail("Invalid email or password.");
        }

        if (!user.EmailConfirmed)
        {
            return AuthResponse.Fail("Please verify your email address before logging in.");
        }

        user.LastLoginAt = DateTime.UtcNow;
        db.Update(user);

        var refresh = _tokens.CreateRefreshToken(out var tokenHash, out var expiresAt);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            CreatedByIp = ipAddress
        });

        await db.SaveChangesAsync(ct);

        return AuthResponse.Ok(ToResponse(user), _tokens.CreateAccessToken(user, user.Tenant), refresh);
    }

    public async Task<AuthResponse> RefreshAsync(
        string refreshToken,
        string? ipAddress,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var tokenHash = _tokens.Hash(refreshToken);
        var token = await db.RefreshTokens
            .Include(r => r.User)
            .ThenInclude(u => u.Tenant)
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);

        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= DateTime.UtcNow)
        {
            return AuthResponse.Fail("The refresh token is invalid or has expired.");
        }

        var user = token.User;
        token.RevokedAt = DateTime.UtcNow;

        var next = _tokens.CreateRefreshToken(out var nextHash, out var nextExpires);
        token.ReplacedByTokenHash = nextHash;

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = nextHash,
            ExpiresAt = nextExpires,
            CreatedByIp = ipAddress
        });

        await db.SaveChangesAsync(ct);

        return AuthResponse.Ok(ToResponse(user), _tokens.CreateAccessToken(user, user.Tenant), next);
    }

    public async Task<bool> LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tokenHash = _tokens.Hash(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);
        if (token is null)
        {
            return false;
        }

        token.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<AuthResponse> VerifyEmailAsync(string token, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var user = await db.AppUsers
            .FirstOrDefaultAsync(u => u.EmailVerificationToken == token, ct);

        if (user is null || user.EmailVerificationTokenExpires < DateTime.UtcNow)
        {
            return AuthResponse.Fail("The verification link is invalid or has expired.");
        }

        user.EmailConfirmed = true;
        user.EmailVerificationToken = null;
        user.EmailVerificationTokenExpires = null;
        await db.SaveChangesAsync(ct);

        return new AuthResponse { Success = true };
    }

    public async Task<bool> ForgotPasswordAsync(string email, string? baseUrl, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null)
        {
            return true;
        }

        user.PasswordResetToken = _tokens.Hash(Guid.NewGuid().ToString("N"));
        user.PasswordResetTokenExpires = DateTime.UtcNow.AddHours(1);
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            await _email.SendPasswordResetAsync(user, $"{baseUrl}/reset-password?token={Uri.EscapeDataString(user.PasswordResetToken)}&email={Uri.EscapeDataString(user.Email)}");
        }

        return true;
    }

    public async Task<AuthResponse> ResetPasswordAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == normalized, ct);

        if (user is null
            || user.PasswordResetToken is null
            || user.PasswordResetTokenExpires < DateTime.UtcNow
            || !string.Equals(user.PasswordResetToken, token, StringComparison.Ordinal))
        {
            return AuthResponse.Fail("The reset link is invalid or has expired.");
        }

        user.PasswordHash = _hasher.HashPassword(user, newPassword);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpires = null;
        await db.SaveChangesAsync(ct);

        return new AuthResponse { Success = true };
    }

    public async Task<AuthResponse> ChangePasswordAsync(
        int userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return AuthResponse.Fail("User not found.");
        }

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) != PasswordVerificationResult.Success)
        {
            return AuthResponse.Fail("Current password is incorrect.");
        }

        user.PasswordHash = _hasher.HashPassword(user, newPassword);
        await db.SaveChangesAsync(ct);

        return new AuthResponse { Success = true };
    }

    private static UserResponse ToResponse(AppUser user) => new()
    {
        Id = user.Id,
        TenantId = user.TenantId,
        Email = user.Email,
        DisplayName = user.DisplayName,
        EmailConfirmed = user.EmailConfirmed,
        IsAdmin = user.IsAdmin
    };
}
