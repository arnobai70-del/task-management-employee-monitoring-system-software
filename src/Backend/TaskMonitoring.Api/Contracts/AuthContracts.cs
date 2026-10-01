using System.ComponentModel.DataAnnotations;

namespace TaskMonitoring.Api.Contracts;

public sealed record LoginRequest(
    [property: Required, EmailAddress, MaxLength(320)] string Email,
    [property: Required, MinLength(8), MaxLength(200)] string Password);

public sealed record RefreshRequest(
    [property: Required, MaxLength(2048)] string RefreshToken);

public sealed record LogoutRequest(
    [property: Required, MaxLength(2048)] string RefreshToken);

public sealed record AuthResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc);

public sealed record WebAuthResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc);

public sealed record ApiError(string Code, string Message);
