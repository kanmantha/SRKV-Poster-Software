using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace DailyPosterGenerator.Services.MultiTenancy;

/// <summary>
/// Resolves the current tenant from the authenticated principal's claims and
/// populates the scoped TenantContext. Cookie MVC requests are already
/// authenticated by UseAuthentication; for API requests carrying a Bearer token
/// the JWT scheme is authenticated here so the tenant claims are available
/// before UseAuthorization runs. Anonymous requests keep the default tenant.
/// </summary>
public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true
            && context.Request.Headers.TryGetValue("Authorization", out var header)
            && header.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
            if (result.Succeeded)
            {
                principal = result.Principal;
                context.User = principal;
            }
        }

        if (principal.Identity?.IsAuthenticated == true)
        {
            tenantContext.IsAuthenticated = true;

            if (int.TryParse(principal.FindFirstValue("tenantId"), out var tenantId))
            {
                tenantContext.TenantId = tenantId;
            }

            if (int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                || int.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out userId))
            {
                tenantContext.UserId = userId;
            }

            tenantContext.UserEmail =
                principal.FindFirstValue(JwtRegisteredClaimNames.Email)
                ?? principal.FindFirstValue(ClaimTypes.Email);

            tenantContext.IsAdmin =
                principal.IsInRole("admin")
                || string.Equals(principal.FindFirstValue("role"), "admin", StringComparison.OrdinalIgnoreCase);
        }

        await _next(context);
    }
}
