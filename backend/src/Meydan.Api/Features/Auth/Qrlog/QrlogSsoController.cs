using Meydan.Api.Common;
using Meydan.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Auth.Qrlog;

public sealed record QrlogStartRequest(string? ReturnTo, bool Link);

/// <summary><see cref="QrlogStartResult"/>: where this device goes, or what a desktop's QR shows, and until when.</summary>
public sealed record QrlogStartResponse(string AuthorizeUrl, string ScanUrl, DateTimeOffset ExpiresAt);

/// <summary>
/// "pending" while QRLog has not vouched; "signed-in" (with the session) or "linked" once; "email-taken" when the
/// address belongs to an unlinked MEYDAN account; "gone" when the ticket ran out, was used or was cancelled.
/// </summary>
public sealed record QrlogClaimResponse(string Status, string ReturnTo, AuthResponse? Session = null, string? Email = null);

/// <summary>
/// "QRLog ilə daxil ol". Four calls: the browser starts a ticket and is sent to QRLog; QRLog's server confirms the
/// ticket over a signed back channel; the browser collects the result with its cookie; or it cancels. No token or
/// identity ever travels through the browser's address bar — only the random ticket code.
/// </summary>
[ApiController]
[Route("api/auth/qrlog")]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class QrlogSsoController(
    QrlogSsoService sso,
    IOptions<AuthCookieOptions> cookieOptions,
    IOptions<JwtOptions> jwt,
    ILogger<QrlogSsoController> logger) : ControllerBase
{
    public const string CookieName = "meydan_qrlog";
    public const string TimestampHeader = "X-QRLog-Timestamp";
    public const string SignatureHeader = "X-QRLog-Signature";

    /// <summary>Opens a ticket. A signed-in caller asking to link gets a link ticket for their own account.</summary>
    [HttpPost("start")]
    public async Task<ActionResult<QrlogStartResponse>> Start(QrlogStartRequest? req, CancellationToken ct)
    {
        Guid? linkUserId = null;
        if (req?.Link == true)
        {
            if (User.Identity?.IsAuthenticated != true)
                return Unauthorized();
            linkUserId = User.GetUserId();
        }
        var started = await sso.StartAsync(req?.ReturnTo, linkUserId, ct);
        Response.Cookies.Append(CookieName, $"{started.Code}.{started.ClaimSecret}", new CookieOptions
        {
            HttpOnly = true,
            Secure = cookieOptions.Value.Secure,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth/qrlog",
            Expires = started.ExpiresAt,
            IsEssential = true,
        });
        return Ok(new QrlogStartResponse(started.AuthorizeUrl, started.ScanUrl, started.ExpiresAt));
    }

    /// <summary>
    /// Called by QRLog's server, never by a browser: the body is signed with the shared secret. The whole raw body is
    /// what is signed, so it is read here rather than model-bound.
    /// </summary>
    [HttpPost("confirm")]
    [DisableRateLimiting]
    public async Task<IActionResult> Confirm(CancellationToken ct)
    {
        if (!sso.Enabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "NotConfigured" });
        if (Request.ContentLength is > QrlogSsoService.MaxBodyBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > QrlogSsoService.MaxBodyBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        var body = buffer.ToArray();

        if (!sso.IsSignatureValid(Request.Headers[TimestampHeader], Request.Headers[SignatureHeader], body))
        {
            // Never which part failed; a tuned attempt is exactly what a precise answer would help.
            logger.LogWarning("QRLog confirmation rejected: signature or timestamp did not verify");
            return Unauthorized(new { error = "InvalidSignature" });
        }

        var payload = QrlogSsoService.ParsePayload(body);
        if (payload is null)
            return BadRequest(new { error = "InvalidPayload" });
        if (!await sso.ConfirmAsync(payload, ct))
            return Conflict(new { error = "CodeExpired" });
        return NoContent();
    }

    /// <summary>Polled by the browser that started the ticket. Without the cookie there is nothing to collect.</summary>
    [HttpPost("claim")]
    [EnableRateLimiting(RateLimitPolicies.QrlogPoll)]
    public async Task<ActionResult<QrlogClaimResponse>> Claim(CancellationToken ct)
    {
        if (!TryReadCookie(out var code, out var secret))
            return NotFound(new ProblemDetails { Status = 404, Title = "Bu brauzerdə başlanmış QRLog girişi yoxdur." });

        var currentUser = User.Identity?.IsAuthenticated == true ? User.GetUserId() : (Guid?)null;
        var result = await sso.ClaimAsync(code, secret, currentUser, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        if (result is null)
            return NotFound(new ProblemDetails { Status = 404, Title = "Bu brauzerdə başlanmış QRLog girişi yoxdur." });

        switch (result.Outcome)
        {
            case QrlogClaimOutcome.Pending:
                return Ok(new QrlogClaimResponse("pending", result.ReturnTo));
            case QrlogClaimOutcome.SignedIn:
                DeleteCookie();
                WriteRefreshCookie(result.Session!);
                return Ok(new QrlogClaimResponse("signed-in", result.ReturnTo, result.Session!.Response));
            case QrlogClaimOutcome.Linked:
                DeleteCookie();
                return Ok(new QrlogClaimResponse("linked", result.ReturnTo));
            case QrlogClaimOutcome.EmailTaken:
                DeleteCookie();
                return Ok(new QrlogClaimResponse("email-taken", result.ReturnTo, Email: result.Email));
            default:
                DeleteCookie();
                return Ok(new QrlogClaimResponse("gone", result.ReturnTo));
        }
    }

    /// <summary>Ends the ticket this browser started (cancelled at QRLog, or asked again).</summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel(CancellationToken ct)
    {
        if (TryReadCookie(out var code, out var secret))
            await sso.EndAsync(code, secret, ct);
        DeleteCookie();
        return NoContent();
    }

    private bool TryReadCookie(out string code, out string secret)
    {
        code = secret = "";
        var raw = Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw)) return false;
        var dot = raw.IndexOf('.');
        if (dot <= 0 || dot == raw.Length - 1) return false;
        code = raw[..dot];
        secret = raw[(dot + 1)..];
        return code.Length is >= 16 and <= 64 && secret.Length is >= 32 and <= 64;
    }

    private void DeleteCookie() => Response.Cookies.Delete(CookieName, new CookieOptions
    {
        HttpOnly = true, Secure = cookieOptions.Value.Secure, SameSite = SameSiteMode.Strict, Path = "/api/auth/qrlog",
    });

    private void WriteRefreshCookie(AuthResult result)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = cookieOptions.Value.Secure,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            IsEssential = true,
        };
        if (result.RefreshToken.IsPersistent)
            options.Expires = result.RefreshToken.ExpiresAt;
        Response.Cookies.Append(jwt.Value.RefreshCookieName, result.RefreshToken.RawToken, options);
    }
}
