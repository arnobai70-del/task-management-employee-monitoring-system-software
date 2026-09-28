using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthService authService) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, ClientIp(), Request.Headers.UserAgent.ToString(), cancellationToken);
        return ToActionResult(result, "Invalid email or password.");
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.RefreshAsync(request, ClientIp(), Request.Headers.UserAgent.ToString(), cancellationToken);
        return ToActionResult(result, "Session is no longer valid. Please sign in again.");
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(subject, out var userId))
        {
            return Unauthorized(new ApiError("invalid_session", "Session is no longer valid. Please sign in again."));
        }

        await authService.RevokeAsync(userId, request.RefreshToken, ClientIp(), cancellationToken);
        return NoContent();
    }

    private ActionResult<AuthResponse> ToActionResult(AuthResult result, string message)
    {
        if (result.Status == AuthStatus.Success && result.Response is not null)
        {
            return Ok(result.Response);
        }

        return Unauthorized(new ApiError("authentication_failed", message));
    }

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
