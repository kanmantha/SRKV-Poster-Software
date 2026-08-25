using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class RegisterRequest
{
    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;

    [StringLength(200)]
    public string? OrganizationName { get; set; }

    [StringLength(50)]
    public string? Sector { get; set; }
}

public class LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public class RefreshRequest
{
    [Required]
    public string RefreshToken { get; set; } = string.Empty;
}

public class VerifyEmailRequest
{
    [Required]
    public string Token { get; set; } = string.Empty;
}

public class ForgotPasswordRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
}

public class ResetPasswordRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Token { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 8)]
    public string NewPassword { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 8)]
    public string NewPassword { get; set; } = string.Empty;
}

public class AuthResponse
{
    public bool Success { get; set; }

    public string? Error { get; set; }

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public string? AccessTokenType => "Bearer";

    public UserResponse? User { get; set; }

    public static AuthResponse Ok(UserResponse user, string accessToken, string refreshToken) =>
        new() { Success = true, User = user, AccessToken = accessToken, RefreshToken = refreshToken };

    public static AuthResponse Fail(string error) => new() { Success = false, Error = error };
}

public class UserResponse
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool EmailConfirmed { get; set; }

    public bool IsAdmin { get; set; }
}
