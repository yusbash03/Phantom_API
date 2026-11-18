using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Auth;

namespace PHANTOM.API.Controllers;

public sealed record LoginResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, CurrentUserResponse User);

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public sealed class AuthController(IAuthService authService, ICurrentUser currentUser, IHostEnvironment environment) : ControllerBase
{
    public const string RefreshCookieName = "phantom_refresh";
    public const string RefreshCookiePath = "/api/v1/auth";

    /// <summary>Signs in. The refresh token is set as an HttpOnly cookie and never returned in the body.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return this.Problem(result.Error!);
        }

        SetRefreshCookie(result.Value.RefreshToken, result.Value.RefreshTokenExpiresAt);
        return Ok(ToResponse(result.Value));
    }

    /// <summary>Rotates the refresh cookie and returns a new access token.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Refresh)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Refresh(CancellationToken cancellationToken)
    {
        var result = await authService.RefreshAsync(Request.Cookies[RefreshCookieName], cancellationToken);
        if (result.IsFailure)
        {
            ClearRefreshCookie();
            return this.Problem(result.Error!);
        }

        SetRefreshCookie(result.Value.RefreshToken, result.Value.RefreshTokenExpiresAt);
        return Ok(ToResponse(result.Value));
    }

    /// <summary>Revokes the session's refresh-token family and clears the cookie.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await authService.LogoutAsync(Request.Cookies[RefreshCookieName], cancellationToken);
        ClearRefreshCookie();
        return NoContent();
    }

    [HttpGet("me")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized();
        }

        return this.ToActionResult(await authService.GetCurrentUserAsync(userId, cancellationToken));
    }

    private static LoginResponse ToResponse(AuthSession session) =>
        new(session.AccessToken, session.AccessTokenExpiresAt, session.User);

    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        // Browsers treat http://localhost as a secure context, but the test server does not run TLS.
        Secure = !environment.IsEnvironment("Testing"),
        SameSite = SameSiteMode.Strict,
        Path = RefreshCookiePath,
        Expires = expires,
        IsEssential = true,
    };

    private void SetRefreshCookie(string token, DateTimeOffset expires) =>
        Response.Cookies.Append(RefreshCookieName, token, CookieOptions(expires));

    private void ClearRefreshCookie() =>
        Response.Cookies.Delete(RefreshCookieName, CookieOptions(null));
}
