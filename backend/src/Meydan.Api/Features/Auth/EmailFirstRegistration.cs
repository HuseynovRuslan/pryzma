using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Meydan.Api.Common;
using Meydan.Api.Features.Email;
using Meydan.Domain;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Email;
using Meydan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Meydan.Api.Features.Auth;

/// <summary>
/// Whether registration starts with the e-mail address (owner, 6 October 2026): the person proves the address first
/// and only then gives a name, a phone and a password, and the account is created already confirmed. Off unless
/// <c>Auth:Registration:EmailFirst</c> is true — and, like e-mail confirmation, only while mail is actually sent.
/// While it is on, the one-step <c>POST /api/auth/register</c> is closed.
/// </summary>
public sealed class EmailFirstRegistrationPolicy(IConfiguration config, EmailConfirmationPolicy mail)
{
    public bool Enabled { get; } = config.GetValue("Auth:Registration:EmailFirst", false) && mail.CanSend;

    /// <summary>How long the link in the mail works, counted from when the mail is composed.</summary>
    public TimeSpan LinkLifetime { get; } = TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Auth:Registration:LinkMinutes", 45), 10, 120));

    /// <summary>How long the completion form may be submitted after the link was opened.</summary>
    public TimeSpan ProofLifetime { get; } = TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Auth:Registration:ProofMinutes", 30), 5, 120));

    /// <summary>An untouched pending registration is swept this long after its last link.</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(24);

    public static readonly TimeSpan ResendGap = TimeSpan.FromMinutes(1);

    /// <summary>Links per address a day: 5 until 21 October 2026, when one person spent them all on mails they could not use.</summary>
    public const int SendsPerDay = 10;
}

public sealed record RegistrationStartRequest
{
    [Required(ErrorMessage = "E-mail ünvanınızı yazın.")]
    [EmailAddress(ErrorMessage = "E-mail ünvanı düzgün deyil.")]
    [StringLength(256)]
    public string Email { get; init; } = "";

    /// <summary>The robot check's token (Cloudflare Turnstile), when the check is on.</summary>
    [StringLength(2048)]
    public string? CaptchaToken { get; init; }
}

public sealed record RegistrationVerifyRequest
{
    [Required(ErrorMessage = "Keçid natamamdır. E-maildəki linki yenidən açın.")]
    [StringLength(200)]
    public string Token { get; init; } = "";
}

/// <summary>The link was good: the completion form may be sent with <paramref name="Proof"/> until <paramref name="ExpiresAt"/>.</summary>
public sealed record RegistrationVerifiedDto(string Proof, string Email, DateTimeOffset ExpiresAt);

public sealed record RegistrationCompleteRequest
{
    [Required(ErrorMessage = "Qeydiyyatın vaxtı bitib. E-mailinizi yenidən təsdiqləyin.")]
    [StringLength(200)]
    public string Proof { get; init; } = "";

    [Required(ErrorMessage = "Adınızı yazın.")]
    [StringLength(80, ErrorMessage = "Ad 80 simvoldan uzun ola bilməz.")]
    public string FirstName { get; init; } = "";

    [Required(ErrorMessage = "Soyadınızı yazın.")]
    [StringLength(80, ErrorMessage = "Soyad 80 simvoldan uzun ola bilməz.")]
    public string LastName { get; init; } = "";

    [Required(ErrorMessage = "Telefon nömrənizi yazın.")]
    [RegularExpression(@"^\+?[0-9 ()\-]{7,20}$", ErrorMessage = "Telefon nömrəsini rəqəmlərlə yazın, məsələn +994 50 123 45 67.")]
    public string Phone { get; init; } = "";

    [Required(ErrorMessage = "Şifrə yazın.")]
    [StringLength(128, MinimumLength = 8, ErrorMessage = "Şifrə ən azı 8 simvol olmalıdır.")]
    public string Password { get; init; } = "";

    [Required(ErrorMessage = "Şifrəni təkrar yazın.")]
    [Compare(nameof(Password), ErrorMessage = "Şifrələr üst-üstə düşmür.")]
    public string ConfirmPassword { get; init; } = "";

    [Range(typeof(bool), "true", "true", ErrorMessage = "Davam etmək üçün şərtlərlə razılaşın.")]
    public bool AcceptTerms { get; init; }
}

/// <summary>
/// Email-first registration, in three steps (see <see cref="PendingRegistration"/>):
/// <list type="number">
/// <item><b>Start</b> — an address and the robot check. No account is created and nothing but the address is kept.
/// An address that already has an account is told so plainly (409) — the owner accepted that the form reveals it
/// (6 October 2026); the robot check and a per-address limit on this endpoint stand against probing. Its owner also
/// gets a mail about the attempt, rationed like a link.</item>
/// <item><b>Verify</b> — the link's one-time token is spent and a proof is issued for the completion form. Asking for
/// another link leaves the earlier ones working until they run out, so any mail of the thread can be opened.</item>
/// <item><b>Complete</b> — name, phone and password; the proof is spent and the account is created confirmed, in one
/// transaction, so a proof makes at most one account and a refused form (a phone in use, a weak password) leaves the
/// proof usable for the corrected form.</item>
/// </list>
/// </summary>
public sealed class EmailFirstRegistration(
    MeydanDbContext db,
    UserManager<ApplicationUser> users,
    IEmailOutbox outbox,
    EmailFirstRegistrationPolicy policy,
    ICaptchaVerifier captcha,
    IOptions<CaptchaOptions> captchaOptions,
    AuthService auth,
    TimeProvider clock,
    ILogger<EmailFirstRegistration> logger)
{
    public const string VerifyKind = EmailTemplates.RegistrationVerifyKind;
    public const string ExistsKind = EmailTemplates.RegistrationExistsKind;

    public const string AccountExists = "Bu e-mail ilə artıq hesab mövcuddur";

    private const string LinkInvalid =
        "Bu link artıq işləmir: ya istifadə olunub, ya da vaxtı bitib. Ən son gələn məktubdakı linki açın və ya yeni link istəyin.";
    private const string ProofInvalid = "Qeydiyyatın vaxtı bitib. E-mailinizi yenidən təsdiqləyin.";

    private void EnsureOn()
    {
        if (!policy.Enabled) throw new NotFoundException("Bu qeydiyyat yolu aktiv deyil.");
    }

    // ---- Step 1 -------------------------------------------------------------------------------------

    public async Task StartAsync(RegistrationStartRequest req, string? ip, CancellationToken ct)
    {
        EnsureOn();
        await CaptchaGate.EnsureAsync(captcha, captchaOptions.Value, req.CaptchaToken, "register", ip, logger, ct);

        var email = req.Email.Trim();
        var normalized = users.NormalizeEmail(email);
        var now = clock.GetUtcNow();

        // Stale rows go first; nothing else ever reads them.
        await db.PendingRegistrations.Where(p => p.ExpiresAt < now).ExecuteDeleteAsync(ct);

        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            // The owner of the account hears about the attempt, rationed like a link; the form is told plainly.
            var dayAgo = now.AddDays(-1);
            var sent = await db.EmailOutbox.AsNoTracking()
                .Where(m => m.UserId == existing.Id && m.Kind == ExistsKind && m.CreatedAt > dayAgo)
                .Select(m => m.CreatedAt).ToListAsync(ct);
            if (sent.Count < EmailFirstRegistrationPolicy.SendsPerDay && sent.All(t => t <= now - EmailFirstRegistrationPolicy.ResendGap))
                await outbox.EnqueueAsync(ExistsKind, existing.Id, null, TimeSpan.FromHours(1), ct);
            logger.LogInformation("Email-first registration asked for an address that already has an account");
            throw new AppException(StatusCodes.Status409Conflict, AccountExists);
        }

        var pending = await db.PendingRegistrations.FirstOrDefaultAsync(p => p.NormalizedEmail == normalized, ct);
        if (pending is null)
        {
            pending = new PendingRegistration
            {
                Id = Guid.NewGuid(), Email = email, NormalizedEmail = normalized, CreatedAt = now,
                SendWindowStartedAt = now, SendCount = 0,
            };
            db.PendingRegistrations.Add(pending);
        }
        else
        {
            if (pending.LastSentAt > now - EmailFirstRegistrationPolicy.ResendGap)
                throw new AppException(StatusCodes.Status429TooManyRequests, "Link az əvvəl göndərildi. Bir dəqiqə sonra yenidən istəyin.");
            if (pending.SendWindowStartedAt <= now.AddDays(-1))
            {
                pending.SendWindowStartedAt = now;
                pending.SendCount = 0;
            }
            if (pending.SendCount >= EmailFirstRegistrationPolicy.SendsPerDay)
                throw new AppException(StatusCodes.Status429TooManyRequests,
                    $"Bu ünvana bu gün {EmailFirstRegistrationPolicy.SendsPerDay} link göndərilib. Gələn məktublardakı linklərdən birini açın " +
                    $"(hər biri {(int)policy.LinkLifetime.TotalMinutes} dəqiqə işləyir) və ya sabah yenidən cəhd edin.");
            // A new link is added to the ones before it; they and any proof already given keep working.
            pending.Email = email;
        }
        pending.SendCount++;
        pending.LastSentAt = now;
        pending.ExpiresAt = now + EmailFirstRegistrationPolicy.PendingLifetime;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another start for the same address won the race a moment ago; its link is on the way.
            throw new AppException(StatusCodes.Status429TooManyRequests, "Link az əvvəl göndərildi. Bir dəqiqə sonra yenidən istəyin.");
        }
        await outbox.EnqueueForPendingRegistrationAsync(VerifyKind, pending.Id, policy.LinkLifetime, ct);
        logger.LogInformation("Email-first registration {PendingId}: link {Count} queued", pending.Id, pending.SendCount);
    }

    // ---- Step 2 -------------------------------------------------------------------------------------

    public async Task<RegistrationVerifiedDto> VerifyAsync(RegistrationVerifyRequest req, CancellationToken ct)
    {
        EnsureOn();
        var tokenHash = Hash(req.Token.Trim());
        var now = clock.GetUtcNow();
        var link = await db.PendingRegistrationLinks.AsNoTracking()
            .Where(l => l.TokenHash == tokenHash && l.ExpiresAt > now)
            .Select(l => new { l.Id, l.PendingRegistrationId })
            .FirstOrDefaultAsync(ct)
            ?? throw ValidationFailedException.For("token", LinkInvalid);

        // Spending the link is the condition of the write: of two requests with the same link, one gets the proof.
        // The registration's other links keep working until they run out.
        var spent = await db.PendingRegistrationLinks.Where(l => l.Id == link.Id).ExecuteDeleteAsync(ct);
        if (spent != 1) throw ValidationFailedException.For("token", LinkInvalid);

        var proof = NewSecret();
        var proofExpires = now + policy.ProofLifetime;
        var confirmed = await db.PendingRegistrations
            .Where(p => p.Id == link.PendingRegistrationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ConfirmedAt, now)
                .SetProperty(p => p.ProofHash, Hash(proof))
                .SetProperty(p => p.ProofExpiresAt, proofExpires), ct);
        // Completed by another link's form in the meantime: the account exists, there is nothing left to confirm.
        if (confirmed != 1) throw ValidationFailedException.For("token", LinkInvalid);
        var email = await db.PendingRegistrations.AsNoTracking()
            .Where(p => p.Id == link.PendingRegistrationId).Select(p => p.Email).FirstAsync(ct);

        logger.LogInformation("Email-first registration {PendingId}: address confirmed", link.PendingRegistrationId);
        return new RegistrationVerifiedDto(proof, email, proofExpires);
    }

    // ---- Step 3 -------------------------------------------------------------------------------------

    public async Task<AuthResult> CompleteAsync(RegistrationCompleteRequest req, string? ip, CancellationToken ct)
    {
        EnsureOn();
        var proofHash = Hash(req.Proof.Trim());
        var now = clock.GetUtcNow();

        if (PhoneNumbers.AzMobile(req.Phone) is null)
            throw ValidationFailedException.For("phone", PhoneNumbers.NotAMobile);

        ApplicationUser? created = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            created = null;
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var pending = await db.PendingRegistrations.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProofHash == proofHash && p.ProofExpiresAt > now, ct)
                ?? throw ValidationFailedException.For("proof", ProofInvalid);
            // Spend the proof first, as the condition of the write: a second form with the same proof waits here for
            // this transaction and then finds nothing to spend. Rolled back with everything else if the form is refused.
            var spent = await db.PendingRegistrations
                .Where(p => p.Id == pending.Id && p.ProofHash == proofHash)
                .ExecuteDeleteAsync(ct);
            if (spent != 1) throw ValidationFailedException.For("proof", ProofInvalid);

            if (await users.FindByEmailAsync(pending.Email) is not null)
                throw ValidationFailedException.For("email", "Bu e-mail ilə artıq hesab var. Daxil olun və ya şifrəni bərpa edin.");
            if (await PhoneNumbers.InUseAsync(users.Users, req.Phone, except: null, ct))
                throw ValidationFailedException.For("phone", PhoneNumbers.AlreadyUsed);

            var user = new ApplicationUser
            {
                UserName = pending.Email,
                Email = pending.Email,
                EmailConfirmed = true,
                FirstName = req.FirstName.Trim(),
                LastName = req.LastName.Trim(),
                PhoneNumber = req.Phone.Trim(),
                TermsAcceptedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                LockoutEnabled = true,
            };
            var result = await users.CreateAsync(user, req.Password);
            if (!result.Succeeded) throw new ValidationFailedException(AuthService.MapIdentityErrors(result.Errors));
            await users.AddToRoleAsync(user, Roles.Participant);
            await tx.CommitAsync(ct);
            created = user;
        });

        logger.LogInformation("User {UserId} registered through email-first registration", created!.Id);
        return await auth.SignInAsync(created, persistent: false, ip, ct);
    }

    // ---- The link, minted when the mail is composed -------------------------------------------------

    /// <summary>
    /// A fresh token for the mail being composed, added to the registration's links; only its hash is kept. Null when
    /// the registration is gone (completed, or swept). A link already opened does not stop it: the person asked again,
    /// most likely because the form from that link was lost.
    /// </summary>
    public static async Task<(string Email, string Token, TimeSpan ValidFor)?> MintLinkAsync(
        MeydanDbContext db, EmailFirstRegistrationPolicy policy, TimeProvider clock, Guid pendingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var pending = await db.PendingRegistrations.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pendingId, ct);
        if (pending is null || pending.ExpiresAt < now) return null;
        var token = NewSecret();
        db.PendingRegistrationLinks.Add(new PendingRegistrationLink
        {
            PendingRegistrationId = pending.Id, TokenHash = Hash(token), CreatedAt = now, ExpiresAt = now + policy.LinkLifetime,
        });
        await db.SaveChangesAsync(ct);
        return (pending.Email, token, policy.LinkLifetime);
    }

    private static string NewSecret() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
