using System.Text.Json;
using System.Text.Json.Serialization;
using Meydan.Api.Common;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Auth;

/// <summary>
/// Configuration section "Captcha". Off unless <see cref="Provider"/> is "Turnstile" and both keys are set; the site
/// key is public (the page shows it), the secret key comes from the environment only.
/// </summary>
public sealed class CaptchaOptions
{
    public const string Section = "Captcha";

    /// <summary>"Turnstile" (Cloudflare) or unset.</summary>
    public string? Provider { get; set; }
    public string? SiteKey { get; set; }
    public string? SecretKey { get; set; }

    /// <summary>
    /// What happens when the provider cannot be asked (network, outage, our own key rejected). False: registration is
    /// refused with "try again" — a fraud wave cannot ride an outage. True: it is let through and logged.
    /// </summary>
    public bool FailOpen { get; set; }

    /// <summary>
    /// Casting a public vote asks for the check too (action "vote") while the check is on. The owner's switch for the
    /// vote alone: false leaves voting unasked and keeps the check on the forms.
    /// </summary>
    public bool OnVote { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 5;
    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    public bool Enabled =>
        string.Equals(Provider, "Turnstile", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(SiteKey) && !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>A vote must carry a passing token.</summary>
    public bool VoteEnabled => Enabled && OnVote;
}

public enum CaptchaOutcome
{
    Passed,
    /// <summary>The person did not pass, or the token is missing, reused, expired or for another action.</summary>
    Failed,
    /// <summary>The provider could not be asked, or refused our own key: nothing is known about the person.</summary>
    Unavailable,
}

/// <summary>The robot check as a gate: does nothing while the check is off; otherwise passes or throws.</summary>
public static class CaptchaGate
{
    public const string Failed = "Robot olmadığınızı təsdiqləyin: yoxlamanı tamamlayıb yenidən göndərin.";

    public static async Task EnsureAsync(
        ICaptchaVerifier verifier, CaptchaOptions options, string? token, string action, string? ip, ILogger logger, CancellationToken ct)
    {
        if (!options.Enabled) return;
        switch (await verifier.VerifyAsync(token, action, ip, ct))
        {
            case CaptchaOutcome.Failed:
                throw ValidationFailedException.For("captcha", Failed);
            case CaptchaOutcome.Unavailable when !options.FailOpen:
                throw new AppException(StatusCodes.Status503ServiceUnavailable,
                    "Yoxlama xidməti hazırda cavab vermir. Bir az sonra yenidən cəhd edin.");
            case CaptchaOutcome.Unavailable:
                logger.LogWarning("Captcha unavailable; let through (Captcha:FailOpen)");
                break;
        }
    }
}

public interface ICaptchaVerifier
{
    /// <param name="action">The widget's action, e.g. "register": a token made for one form is not accepted on another.</param>
    Task<CaptchaOutcome> VerifyAsync(string? token, string action, string? ip, CancellationToken ct);
}

/// <summary>
/// Cloudflare Turnstile's server-side check (siteverify). A token is single-use and lives five minutes; the page asks
/// for a fresh one after every failed submit. Nothing about the person is sent but the token and their address.
/// </summary>
public sealed class TurnstileVerifier(HttpClient http, IOptions<CaptchaOptions> options, ILogger<TurnstileVerifier> logger)
    : ICaptchaVerifier
{
    private sealed record Answer(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);

    /// <summary>Errors that mean our configuration is wrong, not that the person failed.</summary>
    private static readonly string[] OurFault = ["missing-input-secret", "invalid-input-secret", "internal-error"];

    public async Task<CaptchaOutcome> VerifyAsync(string? token, string action, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) return CaptchaOutcome.Failed;
        var o = options.Value;
        var form = new Dictionary<string, string> { ["secret"] = o.SecretKey ?? "", ["response"] = token };
        if (!string.IsNullOrEmpty(ip)) form["remoteip"] = ip;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 30)));
            using var res = await http.PostAsync(o.VerifyUrl, new FormUrlEncodedContent(form), timeout.Token);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Captcha check unavailable: HTTP {Status}", (int)res.StatusCode);
                return CaptchaOutcome.Unavailable;
            }
            var answer = await res.Content.ReadFromJsonAsync<Answer>(timeout.Token);
            if (answer is null) return CaptchaOutcome.Unavailable;
            if (!answer.Success)
            {
                var codes = answer.ErrorCodes ?? [];
                if (codes.Any(OurFault.Contains))
                {
                    logger.LogError("Captcha check rejected our configuration: {Codes}", string.Join(',', codes));
                    return CaptchaOutcome.Unavailable;
                }
                logger.LogInformation("Captcha not passed: {Codes}", string.Join(',', codes));
                return CaptchaOutcome.Failed;
            }
            return string.IsNullOrEmpty(answer.Action) || answer.Action == action ? CaptchaOutcome.Passed : CaptchaOutcome.Failed;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Captcha check unavailable: {ErrorType}", ex.GetType().Name);
            return CaptchaOutcome.Unavailable;
        }
    }
}
