using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DailyPosterGenerator.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    private readonly IAuthService _auth;
    private readonly ILogger<AccountController> _logger;

    public AccountController(IAuthService auth, ILogger<AccountController> logger)
    {
        _auth = auth;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _auth.LoginAsync(model.Email, model.Password, HttpContext.Connection.RemoteIpAddress?.ToString());
        if (!result.Success || result.User is null)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Invalid email or password.");
            return View(model);
        }

        await SignInUserAsync(result.User, model.RememberMe);
        _logger.LogInformation("User {Email} signed in to tenant {TenantId}", result.User.Email, result.User.TenantId);
        return RedirectToLocal(model.ReturnUrl);
    }

    [HttpGet]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        return View(new RegisterViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _auth.RegisterAsync(new RegisterRequest
        {
            Email = model.Email,
            DisplayName = model.DisplayName,
            OrganizationName = model.OrganizationName,
            Sector = model.Sector,
            Password = model.Password
        }, $"{Request.Scheme}://{Request.Host}", HttpContext.Connection.RemoteIpAddress?.ToString());

        if (!result.Success)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Registration failed.");
            return View(model);
        }

        TempData["Success"] = "Your account is ready! Your 14-day free trial has started - log in to get going.";
        return RedirectToAction(nameof(Login));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public async Task<IActionResult> Verify(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return RedirectToAction(nameof(Login));
        }

        var result = await _auth.VerifyEmailAsync(token, ct);
        ViewData["Verified"] = result.Success;
        ViewData["Message"] = result.Error ?? "Your email has been verified. You can now log in.";
        return View();
    }

    [HttpGet]
    public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        await _auth.ForgotPasswordAsync(model.Email, $"{Request.Scheme}://{Request.Host}");
        ViewData["Sent"] = true;
        return View(model);
    }

    [HttpGet]
    public IActionResult ResetPassword(string? token = null, string? email = null)
    {
        return View(new ResetPasswordViewModel
        {
            Token = token ?? string.Empty,
            Email = email ?? string.Empty
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _auth.ResetPasswordAsync(model.Email, model.Token, model.NewPassword);
        if (!result.Success)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Unable to reset the password.");
            return View(model);
        }

        TempData["Success"] = "Your password has been reset. Please log in.";
        return RedirectToAction(nameof(Login));
    }

    private async Task SignInUserAsync(UserResponse user, bool rememberMe)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("tenantId", user.TenantId.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Role, user.IsAdmin ? "admin" : "user"),
            new("role", user.IsAdmin ? "admin" : "user")
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(rememberMe ? 30 : 7)
            });
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "Home");
    }
}