using System.ComponentModel.DataAnnotations;

namespace TaskMonitoring.Api.Contracts;

public sealed record LoginRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MinLength(8), MaxLength(200)] string Password);

public sealed record RefreshRequest(
    [Required, MaxLength(2048)] string RefreshToken);

public sealed record LogoutRequest(
    [Required, MaxLength(2048)] string RefreshToken);

public sealed record AuthResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc);

public sealed record WebAuthResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc);

public sealed record ApiError(string Code, string Message);
