using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DailyPosterGenerator.Models;
using Microsoft.IdentityModel.Tokens;

namespace DailyPosterGenerator.Services.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "DailyPosterGenerator";
    public string Audience { get; set; } = "DailyPosterGenerator";
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 30;
}

public interface IJwtTokenService
{
    string CreateAccessToken(AppUser user, Tenant tenant);

    string CreateRefreshToken(out string tokenHash, out DateTime expiresAt);

    string Hash(string value);

    int GetAccessTokenLifetimeMinutes();
}

public class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(IConfiguration configuration)
    {
        _options = configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
        if (string.IsNullOrWhiteSpace(_options.SigningKey) || _options.SigningKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey must be configured with at least 32 characters.");
        }
    }

    public string CreateAccessToken(AppUser user, Tenant tenant)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim("tenantId", tenant.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Name, user.DisplayName),
            new Claim("role", user.IsAdmin ? "admin" : "user"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateRefreshToken(out string tokenHash, out DateTime expiresAt)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        tokenHash = Hash(raw);
        expiresAt = DateTime.UtcNow.AddDays(_options.RefreshTokenDays);
        return raw;
    }

    public string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public int GetAccessTokenLifetimeMinutes() => _options.AccessTokenMinutes;
}
