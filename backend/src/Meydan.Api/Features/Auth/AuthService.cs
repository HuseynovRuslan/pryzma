using System.Text;
using Meydan.Api.Common;
using Meydan.Domain;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Auth;
using Meydan.Api.Features.Email;
using Meydan.Infrastructure.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Auth;

public sealed record AuthResult(AuthResponse Response, IssuedRefreshToken RefreshToken);

public sealed class AuthService(
    UserManager<ApplicationUser> users,
    AccessTokenService accessTokens,
    RefreshTokenService refreshTokens,
    IEmailOutbox outbox,
    EmailConfirmationPolicy confirmation,
    ICaptchaVerifier captcha,
    Microsoft.Extensions.Options.IOptions<CaptchaOptions> captchaOptions,
    Meydan.Infrastructure.Persistence.MeydanDbContext db,
    IConfiguration config,
    TimeProvider clock,
    ILogger<AuthService> logger)
{
    // The sign-in says which of the three it is (the owner, 27 October 2026). Most refused sign-ins were addresses with
    // no account at all — people arriving from Instagram who took "Daxil ol" for the way in — and the one message for
    // everything sent them round in circles. The registration form already tells whether an address has an account.
    public const string NoAccount = "Bu e-mail ilə hesab yoxdur. Əvvəlcə qeydiyyatdan keçin.";
    public const string WrongPassword = "Şifrə yanlışdır. Unutmusunuzsa, «Şifrəni unutmuşam» ilə yeniləyin.";
    public const string QrlogOnly = "Bu hesab QRLog ilə açılıb və şifrəsi yoxdur. «QRLog ilə daxil ol» düyməsini seçin.";

    public async Task<AuthResult> RegisterAsync(RegisterRequest req, string? ip, CancellationToken ct)
    {
        // With email-first registration on, an account is only ever made from a proven address.
        if (config.GetValue("Auth:Registration:EmailFirst", false) && confirmation.CanSend)
            throw new AppException(StatusCodes.Status409Conflict,
                "Qeydiyyat e-mail təsdiqi ilə başlayır. Səhifəni yeniləyib e-mail ünvanınızı yazın.");

        // The robot check first: it is the one a script cannot pass by changing what it types.
        await CaptchaGate.EnsureAsync(captcha, captchaOptions.Value, req.CaptchaToken, "register", ip, logger, ct);

        // A plausible Azerbaijani mobile, then one phone, one account (D12). Accounts opened before either rule keep
        // working as they are; these checks run only when an account is opened or its number changes.
        if (PhoneNumbers.AzMobile(req.Phone) is null)
            throw ValidationFailedException.For("phone", PhoneNumbers.NotAMobile);
        if (await PhoneNumbers.InUseAsync(users.Users, req.Phone, except: null, ct))
            throw ValidationFailedException.For("phone", PhoneNumbers.AlreadyUsed);

        var now = clock.GetUtcNow();
        var user = new ApplicationUser
        {
            UserName = req.Email.Trim(),
            Email = req.Email.Trim(),
            FirstName = req.FirstName.Trim(),
            LastName = req.LastName.Trim(),
            PhoneNumber = req.Phone.Trim(),
            TermsAcceptedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            LockoutEnabled = true,
        };

        var created = await users.CreateAsync(user, req.Password);
        if (!created.Succeeded)
            throw new ValidationFailedException(MapIdentityErrors(created.Errors));

        await users.AddToRoleAsync(user, Roles.Participant);
        logger.LogInformation("User {UserId} registered", user.Id);
        if (confirmation.Active)
        {
            try
            {
                await QueueConfirmationAsync(user.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The account exists either way; "send a new link" is one click away.
                logger.LogError("Confirmation e-mail for user {UserId} not queued: {ErrorType}", user.Id, ex.GetType().Name);
            }
        }
        return await SignInAsync(user, persistent: false, ip, ct);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest req, string? ip, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is null)
            throw new AppException(StatusCodes.Status401Unauthorized, NoAccount);

        if (await users.IsLockedOutAsync(user))
            throw new AppException(StatusCodes.Status423Locked,
                "Çox sayda uğursuz cəhd oldu. Hesab 10 dəqiqəlik bağlandı. Şifrəni «Şifrəni unutmuşam» ilə yeniləsəniz, " +
                "yeni şifrə ilə dərhal daxil ola bilərsiniz.");

        // An account made through QRLog has no password to get wrong: no try counts against it.
        if (string.IsNullOrEmpty(user.PasswordHash))
            throw new AppException(StatusCodes.Status401Unauthorized, QrlogOnly);

        if (!await users.CheckPasswordAsync(user, req.Password))
        {
            await users.AccessFailedAsync(user);
            throw new AppException(StatusCodes.Status401Unauthorized, WrongPassword);
        }

        await users.ResetAccessFailedCountAsync(user);
        return await SignInAsync(user, req.RememberMe, ip, ct);
    }

    /// <summary>A session for an identity proven elsewhere (QRLog): the same tokens as a password login, no password involved.</summary>
    public async Task<AuthResult> SignInExternalAsync(ApplicationUser user, string? ip, CancellationToken ct)
    {
        if (await users.IsLockedOutAsync(user))
            throw new AppException(StatusCodes.Status423Locked, "Hesab müvəqqəti bağlanıb. Bir az sonra yenidən cəhd edin.");
        return await SignInAsync(user, persistent: false, ip, ct);
    }

    public async Task<AuthResult?> RefreshAsync(string rawToken, string? ip, CancellationToken ct)
    {
        var rotated = await refreshTokens.RotateAsync(rawToken, ip, ct);
        if (rotated is null)
            return null;
        if (await users.IsLockedOutAsync(rotated.User))
            return null;

        var roles = await users.GetRolesAsync(rotated.User);
        var access = accessTokens.Create(rotated.User, roles);
        return new AuthResult(new AuthResponse(access.Token, access.ExpiresAt, ToDto(rotated.User, roles)), rotated.Token);
    }

    public Task LogoutAsync(string rawToken, CancellationToken ct) => refreshTokens.RevokeAsync(rawToken, ct);

    public async Task<UserDto> GetUserAsync(Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("Hesab tapılmadı.");
        return ToDto(user, await users.GetRolesAsync(user));
    }

    /// <summary>
    /// Always succeeds from the caller's view: the endpoint answers the same whether or not the address has an
    /// account. For a real account a durable outbox row is written (user id only); the token and the link are created
    /// when the mail is actually sent, so nothing reusable is stored while it waits.
    /// </summary>
    public async Task ForgotPasswordAsync(ForgotPasswordRequest req, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is null || string.IsNullOrEmpty(user.Email))
            return;
        try
        {
            // A request older than a token's lifetime is not worth sending late.
            await outbox.EnqueueAsync(EmailTemplates.PasswordResetKind, user.Id, null,
                Meydan.Infrastructure.DependencyInjection.PasswordResetLifetime(config), ct);
            logger.LogInformation("Password reset requested for user {UserId}", user.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same answer as for an unknown address; the operator sees the failure.
            logger.LogError("Password reset e-mail for user {UserId} not queued: {ErrorType}", user.Id, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Opens a confirmation link. Confirming twice is not an error; a wrong, changed or expired link is. The session
    /// the account has stays as it is — confirming changes no security stamp.
    /// </summary>
    public async Task ConfirmEmailAsync(ConfirmEmailRequest req, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(req.Token));
        }
        catch (FormatException)
        {
            token = "";
        }
        var invalid = new ValidationFailedException(new Dictionary<string, string[]>
        {
            ["token"] = ["Təsdiq linki etibarsızdır və ya vaxtı bitib. Hesabınıza daxil olub yenisini istəyin."],
        });
        if (user is null || token.Length == 0) throw invalid;
        if (user.EmailConfirmed) return;
        var result = await users.ConfirmEmailAsync(user, token);
        if (!result.Succeeded) throw invalid;
        logger.LogInformation("E-mail confirmed for user {UserId}", user.Id);
    }

    /// <summary>
    /// A new confirmation link for the signed-in account: not within a minute of the last one, at most
    /// <see cref="EmailConfirmationPolicy.ResendsPerDay"/> a day. False when there is nothing to confirm.
    /// </summary>
    public async Task<bool> ResendConfirmationAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString())
                   ?? throw new AppException(StatusCodes.Status401Unauthorized, "Hesabınıza yenidən daxil olun.");
        if (user.EmailConfirmed || string.IsNullOrEmpty(user.Email)) return false;
        if (!confirmation.CanSend)
            throw new UnprocessableException("E-mail göndərişi hazırda işləmir. Bir az sonra yenidən cəhd edin.");

        var now = clock.GetUtcNow();
        var dayAgo = now.AddDays(-1);
        var recent = await db.EmailOutbox.AsNoTracking()
            .Where(m => m.UserId == user.Id && m.Kind == EmailConfirmationPolicy.Kind && m.CreatedAt > dayAgo)
            .Select(m => m.CreatedAt)
            .ToListAsync(ct);
        if (recent.Any(t => t > now - EmailConfirmationPolicy.ResendGap))
            throw new AppException(StatusCodes.Status429TooManyRequests, "Məktub az əvvəl göndərildi. Bir dəqiqə sonra yenidən istəyin.");
        if (recent.Count >= EmailConfirmationPolicy.ResendsPerDay)
            throw new AppException(StatusCodes.Status429TooManyRequests, "Bu gün üçün kifayət qədər məktub göndərildi. Sabah yenidən cəhd edin.");

        await QueueConfirmationAsync(user.Id, ct);
        logger.LogInformation("Confirmation e-mail requested again for user {UserId}", user.Id);
        return true;
    }

    /// <summary>A durable row with the user id only; the token is minted when the mail is sent (EmailComposer).</summary>
    private Task QueueConfirmationAsync(Guid userId, CancellationToken ct) =>
        outbox.EnqueueAsync(EmailConfirmationPolicy.Kind, userId, null,
            Meydan.Infrastructure.DependencyInjection.EmailConfirmationLifetime(config), ct);

    /// <summary>The SPA's public origin (App:PublicUrl); validated as absolute https in production.</summary>
    public static string PublicUrl(IConfiguration config) =>
        (string.IsNullOrWhiteSpace(config["App:PublicUrl"]) ? "http://localhost:5173" : config["App:PublicUrl"]!).TrimEnd('/');

    public async Task ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(req.Token));
        }
        catch (FormatException)
        {
            token = "";
        }

        if (user is null || token.Length == 0)
            throw new ValidationFailedException(new Dictionary<string, string[]>
            {
                ["token"] = ["Keçid etibarsızdır və ya vaxtı bitib. Yenisini istəyin."],
            });

        var result = await users.ResetPasswordAsync(user, token, req.Password);
        if (!result.Succeeded)
            throw new ValidationFailedException(MapIdentityErrors(result.Errors));

        // The lockout that the failed tries with the old password set, and the count towards the next one, go with that
        // password. Otherwise the new password met "Hesab 10 dəqiqəlik bağlandı" and looked as wrong as the old one —
        // which is how people who had just reset it kept trying and gave up (October 2026).
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);

        // A password change ends every existing session: refresh tokens are revoked here, and ResetPasswordAsync
        // rotated the security stamp, which invalidates every access token issued before (SessionStampCheck) as
        // well as this reset token and any other issued before it.
        await refreshTokens.RevokeAllForUserAsync(user.Id, ct);
        logger.LogInformation("Password reset completed for user {UserId}; sessions revoked", user.Id);
    }

    internal async Task<AuthResult> SignInAsync(ApplicationUser user, bool persistent, string? ip, CancellationToken ct)
    {
        var roles = await users.GetRolesAsync(user);
        var access = accessTokens.Create(user, roles);
        var refresh = await refreshTokens.IssueAsync(user.Id, persistent, ip, ct);
        return new AuthResult(new AuthResponse(access.Token, access.ExpiresAt, ToDto(user, roles)), refresh);
    }

    public static UserDto ToDto(ApplicationUser user, IEnumerable<string> roles) =>
        new(user.Id, user.FirstName, user.LastName, user.Email ?? "", user.PhoneNumber, roles.ToList(), user.EmailConfirmed);

    internal static Dictionary<string, string[]> MapIdentityErrors(IEnumerable<IdentityError> errors) =>
        errors
            .GroupBy(e => e.Code switch
            {
                var c when c.StartsWith("Password", StringComparison.Ordinal) => "password",
                "DuplicateEmail" or "DuplicateUserName" or "InvalidEmail" or "InvalidUserName" => "email",
                "InvalidToken" => "token",
                _ => "",
            })
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).Distinct().ToArray());
}
