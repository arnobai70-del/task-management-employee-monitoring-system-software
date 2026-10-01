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
public sealed class AuthController(IAuthService authService, IWebHostEnvironment environment) : ControllerBase
{
    private const string WebRefreshCookieName = "TaskMonitoring.Web.Refresh";
    private const string WebRefreshCookiePath = "/api/auth/web";

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

    [HttpPost("web/login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<WebAuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<WebAuthResponse>> WebLogin(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, ClientIp(), Request.Headers.UserAgent.ToString(), cancellationToken);
        if (result.Status == AuthStatus.Success && result.Response is not null)
        {
            WriteWebRefreshCookie(result.Response.RefreshToken, result.Response.RefreshTokenExpiresAtUtc);
            return Ok(new WebAuthResponse(result.Response.AccessToken, result.Response.AccessTokenExpiresAtUtc));
        }

        DeleteWebRefreshCookie();
        return Unauthorized(new ApiError("authentication_failed", "Invalid email or password."));
    }

    [HttpPost("web/refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<WebAuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<WebAuthResponse>> WebRefresh(CancellationToken cancellationToken)
    {
        var refreshToken = Request.Cookies[WebRefreshCookieName];
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            DeleteWebRefreshCookie();
            return Unauthorized(new ApiError("authentication_failed", "Session is no longer valid. Please sign in again."));
        }

        var result = await authService.RefreshAsync(
            new RefreshRequest(refreshToken),
            ClientIp(),
            Request.Headers.UserAgent.ToString(),
            cancellationToken);
        if (result.Status == AuthStatus.Success && result.Response is not null)
        {
            WriteWebRefreshCookie(result.Response.RefreshToken, result.Response.RefreshTokenExpiresAtUtc);
            return Ok(new WebAuthResponse(result.Response.AccessToken, result.Response.AccessTokenExpiresAtUtc));
        }

        DeleteWebRefreshCookie();
        return Unauthorized(new ApiError("authentication_failed", "Session is no longer valid. Please sign in again."));
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

    [HttpPost("web/logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> WebLogout(CancellationToken cancellationToken)
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(subject, out var userId))
        {
            DeleteWebRefreshCookie();
            return Unauthorized(new ApiError("invalid_session", "Session is no longer valid. Please sign in again."));
        }

        var refreshToken = Request.Cookies[WebRefreshCookieName];
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await authService.RevokeAsync(userId, refreshToken, ClientIp(), cancellationToken);
        }

        DeleteWebRefreshCookie();
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

    private void WriteWebRefreshCookie(string refreshToken, DateTime expiresAtUtc)
    {
        Response.Cookies.Append(WebRefreshCookieName, refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = !environment.IsDevelopment() || Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = WebRefreshCookiePath,
            Expires = expiresAtUtc,
            IsEssential = true
        });
    }

    private void DeleteWebRefreshCookie()
    {
        Response.Cookies.Delete(WebRefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = !environment.IsDevelopment() || Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = WebRefreshCookiePath,
            IsEssential = true
        });
    }

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
