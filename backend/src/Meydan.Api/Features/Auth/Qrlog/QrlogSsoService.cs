using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meydan.Api.Common;
using Meydan.Domain;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Auth.Qrlog;

/// <summary>What QRLog's server posts to /api/auth/qrlog/confirm. Anything else in the body (a role, a tenant, a phone) is ignored.</summary>
public sealed record QrlogConfirmPayload(
    string? Code,
    string? Subject,
    string? FullName,
    string? FirstName,
    string? LastName,
    string? Email,
    bool EmailVerified);

/// <summary>The browser's view of its ticket.</summary>
/// <param name="AuthorizeUrl">This device goes to QRLog and comes back (a phone with QRLog on it).</param>
/// <param name="ScanUrl">
/// What the desktop's QR shows: the same approval page with the ticket code and nothing else — no way back to this
/// browser, no claim secret (that stays in this browser's HttpOnly cookie), no token, no personal data. A photo of the
/// QR can therefore never collect the session.
/// </param>
public sealed record QrlogStartResult(string Code, string ClaimSecret, string AuthorizeUrl, string ScanUrl, DateTimeOffset ExpiresAt);

public enum QrlogClaimOutcome
{
    /// <summary>Nobody has vouched yet — keep polling.</summary>
    Pending,
    SignedIn,
    Linked,
    /// <summary>A MEYDAN account with this e-mail already exists and is not linked: sign in to it, then link.</summary>
    EmailTaken,
    /// <summary>The ticket ran out, was used, or was cancelled.</summary>
    Gone,
}

public sealed record QrlogClaimResult(QrlogClaimOutcome Outcome, string ReturnTo, AuthResult? Session = null, string? Email = null);

public sealed class QrlogSsoService(
    MeydanDbContext db,
    UserManager<ApplicationUser> users,
    AuthService auth,
    AuditTrail audit,
    IOptions<QrlogSsoOptions> options,
    IConfiguration config,
    TimeProvider clock,
    ILogger<QrlogSsoService> logger)
{
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>Administrators sign in with e-mail and password only; QRLog is a convenience for participants.</summary>
    public const string AdminRefused = "İdarəetmə hesabına QRLog ilə daxil olmaq və QRLog bağlamaq olmaz. E-mail və şifrə ilə daxil olun.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public bool Enabled => options.Value.Enabled
                           && options.Value.VouchSecret.Length >= 32
                           && QrlogSsoOptions.IsValidAuthorizeUrl(options.Value.AuthorizeUrl);

    // ---- start ---------------------------------------------------------------------------------

    /// <summary>Opens a ticket. <paramref name="linkUserId"/> makes it a link ticket for that (signed-in) account.</summary>
    public async Task<QrlogStartResult> StartAsync(string? returnTo, Guid? linkUserId, CancellationToken ct)
    {
        EnsureEnabled();
        if (linkUserId is not null
            && await users.FindByIdAsync(linkUserId.Value.ToString()) is { } linking
            && await users.IsInRoleAsync(linking, Roles.Admin))
            throw new AppException(StatusCodes.Status403Forbidden, AdminRefused);
        var now = clock.GetUtcNow();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var secret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var ticket = new QrlogLoginTicket
        {
            Id = Guid.NewGuid(),
            Code = code,
            ClaimSecretHash = HashSecret(secret),
            Purpose = linkUserId is null ? QrlogTicketPurpose.Login : QrlogTicketPurpose.Link,
            LinkUserId = linkUserId,
            ReturnTo = SafeReturnTo(returnTo),
            Status = QrlogTicketStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Math.Clamp(options.Value.TicketMinutes, 1, 30)),
        };
        db.Set<QrlogLoginTicket>().Add(ticket);
        await db.SaveChangesAsync(ct);

        // Yesterday's finished rows go with every new one; nothing keeps them and nothing else runs on a timer for this.
        await db.Set<QrlogLoginTicket>().Where(t => t.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);

        var returnUrl = $"{AuthService.PublicUrl(config)}/login/qrlog";
        var url = options.Value.AuthorizeUrl
            .Replace("{code}", Uri.EscapeDataString(code), StringComparison.Ordinal)
            .Replace("{return}", Uri.EscapeDataString(returnUrl), StringComparison.Ordinal);
        return new QrlogStartResult(code, secret, url, ScanUrl(options.Value.AuthorizeUrl, code), ticket.ExpiresAt);
    }

    /// <summary>
    /// The authorize address without its way back: every query parameter carrying <c>{return}</c> is dropped, so a phone
    /// that scans the desktop's QR approves there and stays there, and the desktop collects the result with its cookie.
    /// </summary>
    public static string ScanUrl(string authorizeTemplate, string code)
    {
        var q = authorizeTemplate.IndexOf('?');
        var path = q < 0 ? authorizeTemplate : authorizeTemplate[..q];
        var kept = q < 0
            ? []
            : authorizeTemplate[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.Contains("{return}", StringComparison.Ordinal))
                .ToArray();
        var url = kept.Length == 0 ? path : $"{path}?{string.Join('&', kept)}";
        return url.Replace("{code}", Uri.EscapeDataString(code), StringComparison.Ordinal);
    }

    // ---- confirm (QRLog's server) ----------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="signature"/> is HMAC-SHA256(secret, timestamp + "\n" + body) and the timestamp is recent.
    /// Constant-time on the signature; nothing here says which part was wrong.
    /// </summary>
    public bool IsSignatureValid(string? timestamp, string? signature, ReadOnlySpan<byte> body)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signature))
            return false;
        if (!DateTimeOffset.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var sent))
            return false;
        var window = TimeSpan.FromMinutes(Math.Clamp(options.Value.SignatureWindowMinutes, 1, 30));
        if ((clock.GetUtcNow() - sent).Duration() > window)
            return false;

        var prefix = Encoding.UTF8.GetBytes(timestamp + "\n");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.VouchSecret), signed));
        var given = signature.Trim().ToUpperInvariant();
        return given.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(given));
    }

    public static QrlogConfirmPayload? ParsePayload(ReadOnlySpan<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize<QrlogConfirmPayload>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Attaches the vouched identity to a pending ticket. False when the code is unknown, used, cancelled or expired.</summary>
    public async Task<bool> ConfirmAsync(QrlogConfirmPayload p, CancellationToken ct)
    {
        EnsureEnabled();
        var code = (p.Code ?? "").Trim().ToLowerInvariant();
        if (code.Length is < 16 or > 64 || !code.All(Uri.IsHexDigit) || !Guid.TryParse(p.Subject, out var subject))
            return false;

        var now = clock.GetUtcNow();
        var ticket = await db.Set<QrlogLoginTicket>().FirstOrDefaultAsync(t => t.Code == code, ct);
        if (ticket is null || ticket.Status != QrlogTicketStatus.Pending || ticket.ExpiresAt <= now)
            return false;

        var (first, last) = SplitName(p);
        ticket.Subject = subject.ToString();
        ticket.FirstName = Clip(first, 120);
        ticket.LastName = Clip(last, 120);
        // Only a well-formed address is kept; QRLog does not verify addresses, so it is never trusted for account matching.
        var email = (p.Email ?? "").Trim();
        ticket.Email = email.Length is > 0 and <= 256 && new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email) ? email : null;
        ticket.Status = QrlogTicketStatus.Confirmed;
        ticket.ConfirmedAt = now;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("QRLog vouched for ticket {TicketId} ({Purpose})", ticket.Id, ticket.Purpose);
        return true;
    }

    // ---- claim (the browser that started it) -------------------------------------------------------

    /// <summary>
    /// Collects the result for the browser holding the cookie. The claim secret is proof of ownership; a wrong one
    /// reads exactly like a ticket that does not exist. The identity is used exactly once.
    /// </summary>
    public async Task<QrlogClaimResult?> ClaimAsync(string code, string secret, Guid? currentUserId, string? ip, CancellationToken ct)
    {
        EnsureEnabled();
        code = code.Trim().ToLowerInvariant();
        var ticket = await db.Set<QrlogLoginTicket>().FirstOrDefaultAsync(t => t.Code == code, ct);
        if (ticket is null || !SecretMatches(ticket, secret))
            return null;

        var now = clock.GetUtcNow();
        if (ticket.Status is QrlogTicketStatus.Claimed or QrlogTicketStatus.Ended || ticket.ExpiresAt <= now)
            return new QrlogClaimResult(QrlogClaimOutcome.Gone, ticket.ReturnTo);
        if (ticket.Status == QrlogTicketStatus.Pending)
            return new QrlogClaimResult(QrlogClaimOutcome.Pending, ticket.ReturnTo);

        // Confirmed: spend it first, so a second reader (a duplicate tab) can never act on it too.
        var subject = ticket.Subject!;
        var identity = (ticket.FirstName ?? "", ticket.LastName ?? "", ticket.Email);
        Spend(ticket, now);
        await db.SaveChangesAsync(ct);

        return ticket.Purpose == QrlogTicketPurpose.Link
            ? await LinkAsync(ticket, subject, currentUserId, ct)
            : await SignInAsync(ticket, subject, identity.Item1, identity.Item2, identity.Email, ip, ct);
    }

    /// <summary>Ends a ticket the browser no longer wants (cancelled at QRLog, signed out, asked again).</summary>
    public async Task EndAsync(string code, string secret, CancellationToken ct)
    {
        code = code.Trim().ToLowerInvariant();
        var ticket = await db.Set<QrlogLoginTicket>().FirstOrDefaultAsync(t => t.Code == code, ct);
        if (ticket is null || !SecretMatches(ticket, secret) || ticket.Status is QrlogTicketStatus.Claimed or QrlogTicketStatus.Ended)
            return;
        Spend(ticket, clock.GetUtcNow());
        ticket.Status = QrlogTicketStatus.Ended;
        await db.SaveChangesAsync(ct);
    }

    private async Task<QrlogClaimResult> SignInAsync(QrlogLoginTicket ticket, string subject, string first, string last, string? email, string? ip, CancellationToken ct)
    {
        var user = await users.FindByLoginAsync(QrlogSsoOptions.Provider, subject);
        if (user is null)
        {
            // No account for this QRLog identity yet. An address is never enough to attach it to an existing account
            // (QRLog does not verify addresses): the person signs in to that account and links QRLog from their profile.
            if (email is not null && await users.FindByEmailAsync(email) is not null)
            {
                logger.LogInformation("QRLog login for ticket {TicketId}: an account with that e-mail exists; explicit linking required", ticket.Id);
                return new QrlogClaimResult(QrlogClaimOutcome.EmailTaken, ticket.ReturnTo, Email: email);
            }
            user = await CreateUserAsync(subject, first, last, email);
            logger.LogInformation("User {UserId} created from QRLog identity", user.Id);
        }

        // MEYDAN's Admin role is granted and used only inside MEYDAN: a QRLog identity never opens an admin session,
        // whatever account it is attached to (a link made before a promotion, or a row put there by hand).
        if (await users.IsInRoleAsync(user, Roles.Admin))
        {
            logger.LogWarning("QRLog login for ticket {TicketId} refused: the mapped account is an administrator", ticket.Id);
            throw new AppException(StatusCodes.Status403Forbidden, AdminRefused);
        }

        var session = await auth.SignInExternalAsync(user, ip, ct);
        logger.LogInformation("User {UserId} signed in with QRLog", user.Id);
        return new QrlogClaimResult(QrlogClaimOutcome.SignedIn, ticket.ReturnTo, session);
    }

    private async Task<QrlogClaimResult> LinkAsync(QrlogLoginTicket ticket, string subject, Guid? currentUserId, CancellationToken ct)
    {
        // Only the session that asked for the link may complete it — and it must still be signed in.
        if (currentUserId is null || ticket.LinkUserId != currentUserId)
            throw new AppException(StatusCodes.Status403Forbidden, "QRLog hesabını bağlamaq üçün əvvəlcə MEYDAN hesabınıza daxil olun.");
        var user = await users.FindByIdAsync(currentUserId.Value.ToString())
                   ?? throw new NotFoundException("Hesab tapılmadı.");
        if (await users.IsInRoleAsync(user, Roles.Admin))
            throw new AppException(StatusCodes.Status403Forbidden, AdminRefused);

        var owner = await users.FindByLoginAsync(QrlogSsoOptions.Provider, subject);
        if (owner is not null && owner.Id != user.Id)
            throw new ConflictException("Bu QRLog hesabı artıq başqa MEYDAN hesabına bağlıdır.");
        if (owner is null)
        {
            var result = await users.AddLoginAsync(user, new UserLoginInfo(QrlogSsoOptions.Provider, subject, QrlogSsoOptions.Provider));
            if (!result.Succeeded)
                throw new ConflictException("QRLog hesabı bağlanmadı. Yenidən cəhd edin.");
            audit.Record(user.Id, AuditActions.QrlogLinked, AuditEntities.Account, user.Id);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("User {UserId} linked a QRLog identity", user.Id);
        }
        return new QrlogClaimResult(QrlogClaimOutcome.Linked, ticket.ReturnTo);
    }

    private async Task<ApplicationUser> CreateUserAsync(string subject, string first, string last, string? email)
    {
        var now = clock.GetUtcNow();
        var user = new ApplicationUser
        {
            // Identity needs a unique user name; without an address the QRLog identity itself is the name.
            UserName = email ?? $"qrlog-{subject}",
            Email = email,
            EmailConfirmed = false,
            FirstName = first.Length > 0 ? first : "QRLog",
            LastName = last.Length > 0 ? last : "istifadəçisi",
            PhoneNumber = null, // not copied from QRLog: nothing here needs it
            // Continuing with QRLog is the acceptance the login page states next to the button.
            TermsAcceptedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            LockoutEnabled = true,
        };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded)
            throw new ConflictException("QRLog hesabı ilə MEYDAN hesabı yaradılmadı. E-mail ilə qeydiyyatdan keçin.");
        await users.AddLoginAsync(user, new UserLoginInfo(QrlogSsoOptions.Provider, subject, QrlogSsoOptions.Provider));
        // Participant only. QRLog roles (admin, manager) mean nothing here; MEYDAN admins are appointed in MEYDAN.
        await users.AddToRoleAsync(user, Roles.Participant);
        return user;
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private void EnsureEnabled()
    {
        if (!Enabled)
            throw new AppException(StatusCodes.Status404NotFound, "QRLog ilə giriş bu saytda aktiv deyil.");
    }

    private static void Spend(QrlogLoginTicket ticket, DateTimeOffset now)
    {
        ticket.Status = QrlogTicketStatus.Claimed;
        ticket.ClaimedAt = now;
        ticket.Subject = null;
        ticket.FirstName = null;
        ticket.LastName = null;
        ticket.Email = null;
    }

    private static bool SecretMatches(QrlogLoginTicket ticket, string secret) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ticket.ClaimSecretHash), Encoding.ASCII.GetBytes(HashSecret(secret)));

    public static string HashSecret(string secret) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Same rule as the SPA's safeNext: a same-site path or nothing.</summary>
    public static string SafeReturnTo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) return "/";
        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith("/\\", StringComparison.Ordinal)) return "/";
        if (value.Any(c => c is '\r' or '\n' or '\0')) return "/";
        return value;
    }

    private static (string First, string Last) SplitName(QrlogConfirmPayload p)
    {
        var first = (p.FirstName ?? "").Trim();
        var last = (p.LastName ?? "").Trim();
        if (first.Length > 0 || last.Length > 0) return (first, last);
        var parts = (p.FullName ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch { 0 => ("", ""), 1 => (parts[0], ""), _ => (parts[0], parts[1]) };
    }

    private static string? Clip(string value, int max) => value.Length == 0 ? null : value.Length <= max ? value : value[..max];
}
