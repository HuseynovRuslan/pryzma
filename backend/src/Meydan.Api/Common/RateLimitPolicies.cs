using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Meydan.Infrastructure.Auth;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Common;

/// <summary>
/// Per-endpoint throttling (config section "RateLimiting"). Anonymous endpoints are partitioned by client IP
/// (the real one — see forwarded-headers setup), participant endpoints by user. Limits are generous for real use
/// and only stop scripted abuse; admin endpoints are not throttled.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Login / registration: brute force and mass sign-up.</summary>
    public const string Auth = "auth";
    /// <summary>Forgot / reset password: mail flooding and token guessing.</summary>
    public const string Password = "password";
    /// <summary>
    /// Silent session refresh (every page load). Many people share one public address (office NAT, mobile carrier
    /// CGNAT, public Wi-Fi), so a request is partitioned by what it carries, not only by where it comes from:
    /// a well-formed refresh cookie → its own session (the token family, stable across rotations — a one-way
    /// fingerprint, never the token); no cookie → the address, generously (answered at once, no database work);
    /// a malformed cookie → the address, tightly (also answered without database work). Behind that sit a coarse
    /// per-address guard for requests with a well-formed cookie and <see cref="RefreshFailureGuard"/>, which stops an
    /// address whose cookies keep matching nothing before it reaches the database again.
    /// </summary>
    public const string Refresh = "refresh";
    /// <summary>Draft create / autosave / final submit.</summary>
    public const string SubmissionWrite = "submission-write";
    /// <summary>File uploads (each up to the configured size).</summary>
    public const string Upload = "upload";
    /// <summary>
    /// Casting a public vote. Partitioned by user, because a vote requires signing in and the database already
    /// refuses a second vote in the same nomination: this only stops a script walking every nomination of every
    /// competition, and is generous enough that nobody voting by hand will ever meet it.
    /// </summary>
    public const string Vote = "vote";
    /// <summary>
    /// The browser polling for its QRLog sign-in result (every few seconds for a few minutes — a desktop showing a QR
    /// polls until the phone approves). Like the refresh: a well-formed ticket cookie → that ticket's own budget (a
    /// one-way fingerprint of its code), so several people signing in behind one office address never share one;
    /// no or a malformed cookie → the address. Plus a coarse per-address guard for requests carrying a ticket cookie.
    /// </summary>
    public const string QrlogPoll = "qrlog-poll";
    /// <summary>
    /// The first step of email-first registration, per address: it says whether an e-mail already has an account
    /// (the owner accepted that on 6 October 2026), so it is the endpoint a prober would hammer, and every call can
    /// send a mail. Only this endpoint — sign-in and voting keep their own limits.
    /// </summary>
    public const string RegistrationStart = "registration-start";

    /// <summary>
    /// A juror signing in with an invitation link and access code, per address — on top of the invitation's own lock
    /// after five wrong codes (Jury.JuryAccess), so a guesser can neither walk the codes of one invitation nor spray
    /// many links from one address.
    /// </summary>
    public const string JuryAuth = "jury-auth";
    /// <summary>A signed-in juror's saves (one per score click, autosaved comments), per address; generous for a real juror.</summary>
    public const string JuryWrite = "jury-write";
    /// <summary>The contact form, per address: each message is an e-mail to the organiser, so a few an hour is plenty.</summary>
    public const string Contact = "contact";

    /// <summary>What a person sees when <see cref="RegistrationStart"/> refuses.</summary>
    public const string RegistrationStartRefused = "Çox sayda cəhd edildi. Bir qədər sonra yenidən yoxlayın.";

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddMeydanRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var s = config.GetSection("RateLimiting");
        var refresh = RefreshLimits.From(s);
        var poll = QrlogPollLimits.From(s);
        services.AddSingleton(_ => new RefreshFailureGuard(refresh.FailedLookupsPerIp));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(Auth, http => Window(ByIp(http), s.GetValue("AuthPermitsPerMinute", 20), OneMinute));
            o.AddPolicy(Password, http => Window(ByIp(http), s.GetValue("PasswordPermitsPer15Minutes", 5), TimeSpan.FromMinutes(15)));
            o.AddPolicy(Refresh, http => RefreshPartition(http, refresh));
            o.AddPolicy(SubmissionWrite, http => Window(ByUser(http), s.GetValue("SubmissionWritesPerMinute", 120), OneMinute));
            o.AddPolicy(Upload, http => Window(ByUser(http), s.GetValue("UploadsPerMinute", 40), OneMinute));
            o.AddPolicy(Vote, http => Window(ByUser(http), s.GetValue("VotesPerMinute", 20), OneMinute));
            o.AddPolicy(QrlogPoll, http => QrlogPollPartition(http, poll));
            o.AddPolicy(RegistrationStart, http =>
                Window("registration-start:" + ByIp(http), s.GetValue("RegistrationStartsPer10Minutes", 10), TimeSpan.FromMinutes(10)));
            o.AddPolicy(JuryAuth, http =>
                Window("jury-auth:" + ByIp(http), s.GetValue("JurySignInsPer15Minutes", 10), TimeSpan.FromMinutes(15)));
            o.AddPolicy(JuryWrite, http => Window("jury-write:" + ByIp(http), s.GetValue("JuryWritesPerMinute", 240), OneMinute));
            o.AddPolicy(Contact, http => Window("contact:" + ByIp(http), s.GetValue("ContactMessagesPerHour", 5), TimeSpan.FromHours(1)));
            // Runs before the endpoint policy; it only ever limits refreshes and QRLog polls that carry a well-formed cookie.
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => AddressGuard(http, refresh, poll));
            o.OnRejected = (ctx, ct) => new ValueTask(WriteTooManyRequestsAsync(
                ctx.HttpContext, ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? (TimeSpan?)after : null, ct,
                ctx.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == RegistrationStart
                    ? RegistrationStartRefused
                    : null));
        });
        return services;
    }

    /// <summary>
    /// The only 429 answer: a Retry-After header and a generic message. Nothing about the token, the session or the
    /// account, and the refresh cookie is left exactly as it was.
    /// </summary>
    public static async Task WriteTooManyRequestsAsync(HttpContext http, TimeSpan? retryAfter, CancellationToken ct, string? title = null)
    {
        var retry = retryAfter is { } after ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)) : 60;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
        http.Response.ContentType = "application/problem+json";
        var minutes = Math.Max(1, (int)Math.Ceiling(retry / 60d));
        await http.Response.WriteAsJsonAsync(new
        {
            status = 429,
            title = title ?? $"Çox sayda sorğu göndərildi. {minutes} dəqiqə sonra yenidən cəhd edin.",
            traceId = http.TraceIdentifier,
        }, ct);
    }

    /// <summary>The client address a limit is kept for (after forwarded headers from the trusted proxy).</summary>
    public static string ByIp(HttpContext http) => "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    private static string ByUser(HttpContext http) =>
        http.User.Identity?.IsAuthenticated == true
            ? "user:" + (http.User.FindFirst("sub")?.Value ?? http.User.Identity.Name ?? "?")
            : ByIp(http);

    private static RateLimitPartition<string> RefreshPartition(HttpContext http, RefreshLimits limits)
    {
        var raw = RefreshCookie(http);
        return RefreshTokenService.Classify(raw) switch
        {
            RefreshTokenShape.Missing => Window("refresh-anonymous:" + ByIp(http), limits.AnonymousPerIp, OneMinute),
            RefreshTokenShape.Malformed => Window("refresh-malformed:" + ByIp(http), limits.MalformedPerIp, OneMinute),
            _ => Window("refresh-session:" + SessionFingerprint(raw!), limits.PerSession, OneMinute),
        };
    }

    private static RateLimitPartition<string> AddressGuard(HttpContext http, RefreshLimits refresh, QrlogPollLimits poll) =>
        http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName switch
        {
            Refresh when RefreshTokenService.Classify(RefreshCookie(http)) is RefreshTokenShape.Current or RefreshTokenShape.Legacy
                => Window("refresh-address:" + ByIp(http), refresh.PerIp, OneMinute),
            QrlogPoll when TicketCode(http) is not null
                => Window("qrlog-address:" + ByIp(http), poll.PerIp, OneMinute),
            _ => RateLimitPartition.GetNoLimiter("-"),
        };

    private static RateLimitPartition<string> QrlogPollPartition(HttpContext http, QrlogPollLimits limits) =>
        TicketCode(http) is { } code
            ? Window("qrlog-ticket:" + PollFingerprint(code), limits.PerTicket, OneMinute)
            : Window("qrlog-poll:" + ByIp(http), limits.AnonymousPerIp, OneMinute);

    /// <summary>
    /// The code part of a well-formed QRLog ticket cookie ("&lt;32 hex code&gt;.&lt;43 base64url secret&gt;"), else null.
    /// Only the code — which is not a secret — is ever fingerprinted; the claim secret is never looked at here.
    /// </summary>
    private static string? TicketCode(HttpContext http)
    {
        var raw = http.Request.Cookies[Features.Auth.Qrlog.QrlogSsoController.CookieName];
        return raw is { Length: 76 } && raw[32] == '.' && raw.AsSpan(0, 32).IndexOfAnyExcept("0123456789abcdef") < 0
            && raw.AsSpan(33).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0
            ? raw[..32]
            : null;
    }

    private static string PollFingerprint(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("meydan/qrlog-poll-throttle/v1\n" + code)), 0, 16);

    private static string? RefreshCookie(HttpContext http) =>
        http.Request.Cookies[http.RequestServices.GetRequiredService<IOptions<JwtOptions>>().Value.RefreshCookieName];

    /// <summary>
    /// The session a refresh belongs to, as a one-way fingerprint: the family for current tokens (stable across
    /// rotations, so a session that hammers refresh meets its own limit), the token itself for legacy ones (they
    /// rotate into the current form on first use). Domain-separated, so it never equals the stored token hash.
    /// </summary>
    public static string SessionFingerprint(string raw)
    {
        var subject = RefreshTokenService.TryGetFamilyKey(raw, out var family) ? "family:" + family : "token:" + raw;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("meydan/refresh-throttle/v1\n" + subject)), 0, 16);
    }

    private static RateLimitPartition<string> Window(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permits),
            Window = window,
            QueueLimit = 0,
        });

    /// <summary>QRLog result polling limits, all per minute (config "RateLimiting").</summary>
    private sealed record QrlogPollLimits(int PerTicket, int PerIp, int AnonymousPerIp)
    {
        public static QrlogPollLimits From(IConfigurationSection s) => new(
            s.GetValue("QrlogPollPermitsPerTicketPerMinute", 40),
            s.GetValue("QrlogPollPermitsPerIpPerMinute", 1200),
            s.GetValue("QrlogPollPermitsPerMinute", 90));
    }

    /// <summary>Refresh limits, all per minute (config "RateLimiting").</summary>
    private sealed record RefreshLimits(int PerSession, int PerIp, int AnonymousPerIp, int MalformedPerIp, int FailedLookupsPerIp)
    {
        public static RefreshLimits From(IConfigurationSection s) => new(
            s.GetValue("RefreshPermitsPerSessionPerMinute", 20),
            s.GetValue("RefreshPermitsPerIpPerMinute", 1200),
            s.GetValue("AnonymousRefreshPermitsPerIpPerMinute", 600),
            s.GetValue("MalformedRefreshPermitsPerIpPerMinute", 30),
            s.GetValue("FailedRefreshesPerIpPerMinute", 60));
    }
}

/// <summary>
/// Coarse per-address guard in front of the refresh lookup. Only failed lookups count — a well-formed cookie that
/// matches no live token (junk, a revoked or expired session) — so the ordinary refreshes of many people behind one
/// address never use it up. An address with too many failures in a minute is refused before any further lookup.
/// </summary>
public sealed class RefreshFailureGuard(int failuresPerMinute) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _failures = PartitionedRateLimiter.Create<string, string>(address =>
        RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, failuresPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    /// <summary>False when the address has used up its failures; <paramref name="retryAfter"/> says for how long.</summary>
    public bool MayLookUp(string address, out TimeSpan? retryAfter)
    {
        // Asking for zero permits only checks whether any are left.
        using var probe = _failures.AttemptAcquire(address, 0);
        retryAfter = !probe.IsAcquired && probe.TryGetMetadata(MetadataName.RetryAfter, out var after) ? (TimeSpan?)after : null;
        return probe.IsAcquired;
    }

    public void RecordFailure(string address) => _failures.AttemptAcquire(address, 1).Dispose();

    public void Dispose() => _failures.Dispose();
}
