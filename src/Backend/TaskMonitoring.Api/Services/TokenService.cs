using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface ITokenService
{
    IssuedTokens Issue(User user, IEnumerable<string> roles, IEnumerable<string> permissions, DateTime utcNow);
    string HashRefreshToken(string token);
}

public sealed record IssuedTokens(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    string RefreshTokenHash,
    DateTime RefreshTokenExpiresAtUtc);

public sealed class TokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public IssuedTokens Issue(User user, IEnumerable<string> roles, IEnumerable<string> permissions, DateTime utcNow)
    {
        var accessExpiry = utcNow.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        claims.AddRange(roles.Distinct(StringComparer.Ordinal).Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(permissions.Distinct(StringComparer.Ordinal).Select(permission => new Claim("permission", permission)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: utcNow,
            expires: accessExpiry,
            signingCredentials: credentials);

        var refreshToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));
        return new IssuedTokens(
            new JwtSecurityTokenHandler().WriteToken(jwt),
            accessExpiry,
            refreshToken,
            HashRefreshToken(refreshToken),
            utcNow.AddDays(_options.RefreshTokenDays));
    }

    public string HashRefreshToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}
