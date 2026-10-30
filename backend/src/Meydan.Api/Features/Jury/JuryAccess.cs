using System.Security.Cryptography;
using System.Text;
using Meydan.Api.Common;
using Meydan.Api.Features.Auth;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Jury;

/// <summary>
/// How a juror gets in, and stays in, with nothing a guess or a name can open.
/// <list type="bullet">
/// <item>The admin issues a personal invitation per juror: a link whose secret rides in the URL's #fragment (browsers
/// never send a fragment, so it reaches no server log, proxy or Referer) and a separate access code the admin gives the
/// juror another way. Only hashes are stored — SHA-256 of the 256-bit link secret, PBKDF2 of the code.</item>
/// <item>Signing in needs both. Wrong codes are counted: at <see cref="MaxCodeAttempts"/> the invitation locks until the
/// admin reissues it. The endpoint is also throttled per address. Invitations expire and can be revoked; reissuing one
/// ends every earlier session of that juror.</item>
/// <item>The session is an HttpOnly, SameSite=Strict cookie scoped to /api/jury, stored only as a hash, valid for
/// <see cref="SessionLifetime"/>. It opens the juror's own evaluation and nothing else — no admin API takes it.</item>
/// </list>
/// </summary>
public sealed class JuryAccess(MeydanDbContext db, AuditTrail audit, TimeProvider clock, IOptions<AuthCookieOptions> cookies)
{
    public const string CookieName = "meydan_jury";
    public const string CookiePath = "/api/jury";
    public const int MaxCodeAttempts = 5;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private const int Pbkdf2Iterations = 100_000;

    public const string InvalidInvitation = "Dəvət etibarlı deyil, müddəti bitib və ya ləğv olunub. Adminə müraciət edin.";

    // ---- Secrets -----------------------------------------------------------------------------------------------

    /// <summary>256 random bits, URL-safe.</summary>
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Six digits, from the system's cryptographic generator.</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Digits only, so "123 456" and "123-456" are the code as written on paper.</summary>
    public static string NormalizeCode(string? code) => new((code ?? "").Where(char.IsAsciiDigit).ToArray());

    public static string HashCode(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(NormalizeCode(code), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyCode(string code, string? stored)
    {
        var parts = (stored ?? "").Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(NormalizeCode(code), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string InvitationState(JuryAssignment a, DateTimeOffset now) =>
        a.InviteTokenHash is null ? "None"
        : a.InviteRevokedAt is not null ? "Revoked"
        : a.FailedCodeAttempts >= MaxCodeAttempts ? "Locked"
        : a.InviteExpiresAt <= now ? "Expired"
        : "Active";

    // ---- The admin's side --------------------------------------------------------------------------------------

    /// <summary>A new invitation for the juror (any earlier one and all their sessions end). Its secrets are returned once.</summary>
    public async Task<(string Secret, string Code, DateTimeOffset ExpiresAt)> IssueAsync(
        JuryAssignment assignment, int days, Guid adminId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var secret = NewSecret();
        var code = NewCode();
        assignment.InviteTokenHash = Sha256(secret);
        assignment.InviteCodeHash = HashCode(code);
        assignment.InviteIssuedAt = now;
        assignment.InviteIssuedById = adminId;
        assignment.InviteExpiresAt = now.AddDays(days);
        assignment.InviteRevokedAt = null;
        assignment.FailedCodeAttempts = 0;
        await EndSessionsAsync(assignment.Id, now, ct);
        audit.Record(adminId, AuditActions.JuryInvitationIssued, AuditEntities.Competition, assignment.Evaluation.CompetitionId,
            $"{assignment.Juror.Label} (#{assignment.JurorId}): invitation issued, expires {assignment.InviteExpiresAt:O}");
        return (secret, $"{code[..3]} {code[3..]}", assignment.InviteExpiresAt.Value);
    }

    public async Task RevokeAsync(JuryAssignment assignment, Guid adminId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        assignment.InviteRevokedAt = now;
        await EndSessionsAsync(assignment.Id, now, ct);
        audit.Record(adminId, AuditActions.JuryInvitationRevoked, AuditEntities.Competition, assignment.Evaluation.CompetitionId,
            $"{assignment.Juror.Label} (#{assignment.JurorId}): invitation revoked, sessions ended");
    }

    private Task EndSessionsAsync(int assignmentId, DateTimeOffset now, CancellationToken ct) =>
        db.JurySessions.Where(s => s.AssignmentId == assignmentId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);

    // ---- The juror's side --------------------------------------------------------------------------------------

    /// <summary>
    /// Signs a juror in with the link's secret and the access code; sets the session cookie. Every refusal says the same
    /// thing, so it tells a guesser nothing about which half was wrong — except that a locked invitation says so.
    /// </summary>
    public async Task<JuryAssignment> SignInAsync(HttpContext http, JurorSignInRequest req, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = Sha256((req.Token ?? "").Trim());
        var a = await db.JuryAssignments.Include(x => x.Evaluation).ThenInclude(e => e.Competition).Include(x => x.Juror)
                    .FirstOrDefaultAsync(x => x.InviteTokenHash == hash, ct)
                ?? throw new UnauthorizedException(InvalidInvitation);
        if (a.InviteRevokedAt is not null || a.InviteExpiresAt is null || a.InviteExpiresAt <= now)
            throw new UnauthorizedException(InvalidInvitation);
        if (a.FailedCodeAttempts >= MaxCodeAttempts)
            throw new UnauthorizedException("Kod çox dəfə səhv yazılıb: dəvət kilidlənib. Adminə müraciət edin.");
        if (!VerifyCode(req.Code ?? "", a.InviteCodeHash))
        {
            a.FailedCodeAttempts++;
            audit.Record(null, AuditActions.JurorSignInFailed, AuditEntities.Competition, a.Evaluation.CompetitionId,
                $"{a.Juror.Label} (#{a.JurorId}): wrong access code ({a.FailedCodeAttempts}/{MaxCodeAttempts})");
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(a.FailedCodeAttempts >= MaxCodeAttempts
                ? "Kod çox dəfə səhv yazılıb: dəvət kilidlənib. Adminə müraciət edin."
                : "Giriş kodu səhvdir.");
        }

        var raw = NewSecret();
        db.JurySessions.Add(new JurySession
        {
            AssignmentId = a.Id, TokenHash = Sha256(raw), CreatedAt = now, ExpiresAt = now.Add(SessionLifetime), LastSeenAt = now,
        });
        a.FailedCodeAttempts = 0;
        a.LastSignInAt = now;
        audit.Record(null, AuditActions.JurorSignedIn, AuditEntities.Competition, a.Evaluation.CompetitionId,
            $"{a.Juror.Label} (#{a.JurorId}) signed in");
        await db.SaveChangesAsync(ct);
        http.Response.Cookies.Append(CookieName, raw, Cookie(now.Add(SessionLifetime)));
        return a;
    }

    /// <summary>The signed-in juror's assignment (with its evaluation and juror), or 401.</summary>
    public async Task<JuryAssignment> RequireAsync(HttpContext http, CancellationToken ct)
    {
        var raw = http.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw) || raw.Length > 100) throw new UnauthorizedException("Münsif kimi daxil olun.");
        var now = clock.GetUtcNow();
        var hash = Sha256(raw);
        var session = await db.JurySessions
            .Include(s => s.Assignment).ThenInclude(a => a.Evaluation).ThenInclude(e => e.Competition)
            .Include(s => s.Assignment).ThenInclude(a => a.Juror)
            .FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now
            || session.Assignment.InviteRevokedAt is not null)
            throw new UnauthorizedException("Sessiya bitib. Dəvət linki ilə yenidən daxil olun.");
        // Seen-at only every few minutes: a page of thumbnails must not write a row per image.
        if (now - session.LastSeenAt > TimeSpan.FromMinutes(5))
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }
        return session.Assignment;
    }

    public async Task SignOutAsync(HttpContext http, CancellationToken ct)
    {
        var raw = http.Request.Cookies[CookieName];
        if (!string.IsNullOrEmpty(raw))
        {
            var hash = Sha256(raw);
            var session = await db.JurySessions.Include(s => s.Assignment).ThenInclude(a => a.Juror)
                .Include(s => s.Assignment).ThenInclude(a => a.Evaluation)
                .FirstOrDefaultAsync(s => s.TokenHash == hash && s.RevokedAt == null, ct);
            if (session is not null)
            {
                session.RevokedAt = clock.GetUtcNow();
                audit.Record(null, AuditActions.JurorSignedOut, AuditEntities.Competition, session.Assignment.Evaluation.CompetitionId,
                    $"{session.Assignment.Juror.Label} (#{session.Assignment.JurorId}) signed out");
                await db.SaveChangesAsync(ct);
            }
        }
        http.Response.Cookies.Delete(CookieName, Cookie(null));
    }

    private CookieOptions Cookie(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = cookies.Value.Secure,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        IsEssential = true,
        Expires = expires,
    };
}
