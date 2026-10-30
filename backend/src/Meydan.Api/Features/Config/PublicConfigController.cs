using Meydan.Api.Features.Auth;
using Meydan.Api.Features.Auth.Qrlog;
using Meydan.Infrastructure.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Config;

/// <summary>What the SPA needs to know about this deployment before anyone signs in: nothing secret, only which doors are open.</summary>
public sealed record PublicConfigDto(
    /// <summary>"QRLog ilə daxil ol" is offered.</summary>
    bool QrlogLogin,
    /// <summary>Password-reset e-mails can actually be delivered (a mail provider is configured).</summary>
    bool PasswordReset,
    /// <summary>The robot check's public site key when registration asks for it; null when it does not.</summary>
    string? CaptchaSiteKey = null,
    /// <summary>"email-first": registration starts with the address and a link; "classic": one form.</summary>
    string RegistrationFlow = "classic",
    /// <summary>Casting a vote asks for the robot check too, with <see cref="CaptchaSiteKey"/>.</summary>
    bool VoteCaptcha = false);

[ApiController]
[Route("api/config")]
[DisableRateLimiting]
public sealed class PublicConfigController(
    QrlogSsoService sso, IOptions<EmailOptions> email, IOptions<CaptchaOptions> captcha, IHostEnvironment env,
    EmailFirstRegistrationPolicy registration) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true)]
    public PublicConfigDto Get() =>
        new(sso.Enabled, email.Value.EffectiveMode(env) != EmailMode.Disabled,
            captcha.Value.Enabled ? captcha.Value.SiteKey : null,
            registration.Enabled ? "email-first" : "classic",
            captcha.Value.VoteEnabled);
}
