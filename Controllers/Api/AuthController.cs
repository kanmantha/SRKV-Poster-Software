using System.Security.Claims;
using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services.Auth;
using DailyPosterGenerator.Services.MultiTenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Controllers.Api;

[Route("api/auth")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth)
    {
        _auth = auth;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        return Ok(await _auth.RegisterAsync(request, BaseUrl, Ip, ct));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        return Ok(await _auth.LoginAsync(request.Email, request.Password, Ip, ct));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken ct)
    {
        return Ok(await _auth.RefreshAsync(request.RefreshToken, Ip, ct));
    }

    [HttpPost("verify-email")]
    public async Task<ActionResult<AuthResponse>> VerifyEmail(VerifyEmailRequest request, CancellationToken ct)
    {
        return Ok(await _auth.VerifyEmailAsync(request.Token, ct));
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await _auth.ForgotPasswordAsync(request.Email, BaseUrl, ct);
        return Ok(new { message = "If that email is registered, a reset link has been sent." });
    }

    [HttpPost("reset-password")]
    public async Task<ActionResult<AuthResponse>> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        return Ok(await _auth.ResetPasswordAsync(request.Email, request.Token, request.NewPassword, ct));
    }

    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct)
    {
        await _auth.LogoutAsync(request.RefreshToken, ct);
        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<AuthResponse>> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        return Ok(await _auth.ChangePasswordAsync(userId.Value, request.CurrentPassword, request.NewPassword, ct));
    }

    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<object>> Me(CancellationToken ct)
    {
        var tenantContext = HttpContext.RequestServices.GetRequiredService<TenantContext>();
        var factory = HttpContext.RequestServices.GetRequiredService<IDbContextFactory<DailyPosterDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        var user = await db.AppUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == tenantContext.UserId, ct);

        if (user is null)
        {
            return Unauthorized();
        }

        return Ok(new
        {
            user.Id,
            user.Email,
            user.DisplayName,
            user.EmailConfirmed,
            user.IsAdmin,
            user.TenantId,
            Subscription = new
            {
                Status = tenantContext.SubscriptionStatus.ToString(),
                tenantContext.PlanCode,
                tenantContext.CreditsRemaining,
                PeriodEnd = tenantContext.PeriodEnd,
                TrialEndsAt = tenantContext.TrialEndsAt
            }
        });
    }

    private int? GetUserId()
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idClaim, out var id) ? id : null;
    }
}
