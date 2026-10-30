using Meydan.Api.Common;
using Meydan.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Auth;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(
    AuthService auth,
    EmailFirstRegistration emailFirst,
    RefreshFailureGuard refreshFailures,
    IOptions<JwtOptions> jwt,
    IOptions<AuthCookieOptions> cookieOptions) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req, CancellationToken ct)
    {
        var result = await auth.RegisterAsync(req, ClientIp, ct);
        WriteRefreshCookie(result);
        return Ok(result.Response);
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        var result = await auth.LoginAsync(req, ClientIp, ct);
        WriteRefreshCookie(result);
        return Ok(result.Response);
    }

    /// <summary>
    /// Silent session restore: the SPA calls this on load with the HttpOnly refresh cookie. Throttling is layered
    /// (see <see cref="RateLimitPolicies.Refresh"/>); a 429 never touches the cookie or the session.
    /// </summary>
    [HttpPost("refresh")]
    [EnableRateLimiting(RateLimitPolicies.Refresh)]
    public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken ct)
    {
        var raw = Request.Cookies[jwt.Value.RefreshCookieName];
        switch (RefreshTokenService.Classify(raw))
        {
            case RefreshTokenShape.Missing:
                return Unauthorized();
            case RefreshTokenShape.Malformed:
                // Nothing ever issued looks like this: answered without touching the database.
                DeleteRefreshCookie();
                return Unauthorized();
        }

        // An address whose cookies keep matching nothing is refused before it reaches the database again.
        var address = RateLimitPolicies.ByIp(HttpContext);
        if (!refreshFailures.MayLookUp(address, out var retryAfter))
        {
            await RateLimitPolicies.WriteTooManyRequestsAsync(HttpContext, retryAfter, ct);
            return new EmptyResult();
        }

        var result = await auth.RefreshAsync(raw!, ClientIp, ct);
        if (result is null)
        {
            refreshFailures.RecordFailure(address);
            DeleteRefreshCookie();
            return Unauthorized();
        }
        WriteRefreshCookie(result);
        return Ok(result.Response);
    }

    [HttpPost("logout")]
    [DisableRateLimiting]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var raw = Request.Cookies[jwt.Value.RefreshCookieName];
        if (!string.IsNullOrEmpty(raw))
            await auth.LogoutAsync(raw, ct);
        DeleteRefreshCookie();
        return NoContent();
    }

    [HttpPost("forgot-password")]
    [EnableRateLimiting(RateLimitPolicies.Password)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest req, CancellationToken ct)
    {
        await auth.ForgotPasswordAsync(req, ct);
        return NoContent();
    }

    [HttpPost("reset-password")]
    [EnableRateLimiting(RateLimitPolicies.Password)]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest req, CancellationToken ct)
    {
        await auth.ResetPasswordAsync(req, ct);
        return NoContent();
    }

    /// <summary>
    /// Email-first registration, step 1: an address and the robot check. 202 when a link is on its way; 409 when the
    /// address already has an account. Its own per-address limit (<see cref="RateLimitPolicies.RegistrationStart"/>).
    /// </summary>
    [HttpPost("registration/start")]
    [EnableRateLimiting(RateLimitPolicies.RegistrationStart)]
    public async Task<IActionResult> StartRegistration(RegistrationStartRequest req, CancellationToken ct)
    {
        await emailFirst.StartAsync(req, ClientIp, ct);
        return Accepted();
    }

    /// <summary>Step 2: the link from the mail; answers with the proof the completion form needs.</summary>
    [HttpPost("registration/verify")]
    [EnableRateLimiting(RateLimitPolicies.Password)]
    public Task<RegistrationVerifiedDto> VerifyRegistration(RegistrationVerifyRequest req, CancellationToken ct) =>
        emailFirst.VerifyAsync(req, ct);

    /// <summary>Step 3: name, phone and password; creates the account (already confirmed) and signs it in.</summary>
    [HttpPost("registration/complete")]
    public async Task<ActionResult<AuthResponse>> CompleteRegistration(RegistrationCompleteRequest req, CancellationToken ct)
    {
        var result = await emailFirst.CompleteAsync(req, ClientIp, ct);
        WriteRefreshCookie(result);
        return Ok(result.Response);
    }

    [HttpPost("confirm-email")]
    [EnableRateLimiting(RateLimitPolicies.Password)]
    public async Task<IActionResult> ConfirmEmail(ConfirmEmailRequest req, CancellationToken ct)
    {
        await auth.ConfirmEmailAsync(req, ct);
        return NoContent();
    }

    /// <summary>A new confirmation link for the signed-in account; 202 when one is on its way, 204 when nothing is left to confirm.</summary>
    [Authorize]
    [HttpPost("email-confirmation")]
    public async Task<IActionResult> ResendConfirmation(CancellationToken ct) =>
        await auth.ResendConfirmationAsync(User.GetUserId(), ct) ? Accepted() : NoContent();

    [Authorize]
    [HttpGet("me")]
    [DisableRateLimiting]
    public Task<UserDto> Me() => auth.GetUserAsync(User.GetUserId());

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private void WriteRefreshCookie(AuthResult result)
    {
        var options = BaseCookie();
        // Non-persistent logins get a browser-session cookie; the server-side expiry still applies.
        if (result.RefreshToken.IsPersistent)
            options.Expires = result.RefreshToken.ExpiresAt;
        Response.Cookies.Append(jwt.Value.RefreshCookieName, result.RefreshToken.RawToken, options);
    }

    private void DeleteRefreshCookie() => Response.Cookies.Delete(jwt.Value.RefreshCookieName, BaseCookie());

    private CookieOptions BaseCookie() => new()
    {
        HttpOnly = true,
        Secure = cookieOptions.Value.Secure,
        SameSite = SameSiteMode.Strict,
        Path = "/api/auth",
        IsEssential = true,
    };
}

public sealed class AuthCookieOptions
{
    public const string Section = "AuthCookie";

    /// <summary>Must be true outside local development (HTTPS only).</summary>
    public bool Secure { get; set; } = true;
}
