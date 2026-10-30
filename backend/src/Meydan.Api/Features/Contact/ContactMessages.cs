using System.Text.RegularExpressions;
using Meydan.Api.Common;
using Meydan.Api.Features.Auth;
using Meydan.Api.Features.Email;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Email;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Meydan.Api.Features.Contact;

/// <summary>Configuration section "Contact".</summary>
public sealed class ContactOptions
{
    public const string Section = "Contact";

    /// <summary>Where the contact form's messages go (the owner's choice, 19 October 2026). Empty: the form is refused.</summary>
    public string? ToAddress { get; set; }
}

/// <summary>
/// The site's contact form: one message, delivered at once by e-mail to the organiser, from our own address with
/// "Reply" set to the person who wrote. Nothing of it is stored — like every mail here, its body is never persisted —
/// so it is sent while the person waits, and a failure is said to them rather than swallowed. Behind the robot check
/// (when on) and a per-address limit (<see cref="RateLimitPolicies.Contact"/>); the recipient is fixed by
/// configuration, so the form can never be used to mail anyone else.
/// </summary>
public sealed partial class ContactMessages(
    MeydanDbContext db,
    IEmailSender sender,
    ICaptchaVerifier captcha,
    IOptions<CaptchaOptions> captchaOptions,
    IOptions<ContactOptions> options,
    ILogger<ContactMessages> logger)
{
    public const string CaptchaAction = "contact";
    public const string Unavailable = "Mesaj hazırda göndərilə bilmir. Bir az sonra yenidən cəhd edin və ya bizə e-poçtla yazın.";
    public const string NotSent = "Mesajınız göndərilmədi. Bir az sonra yenidən cəhd edin.";

    public async Task SendAsync(ContactRequest req, string? ip, CancellationToken ct)
    {
        var to = options.Value.ToAddress;
        // Never pretend: with nowhere to send it, or mail switched off, the form says so instead of losing the message.
        if (string.IsNullOrWhiteSpace(to) || sender is DisabledEmailSender)
            throw new AppException(StatusCodes.Status503ServiceUnavailable, Unavailable);

        await CaptchaGate.EnsureAsync(captcha, captchaOptions.Value, req.CaptchaToken, CaptchaAction, ip, logger, ct);

        var email = req.Email.Trim();
        // One plain address, exactly as typed: it becomes the Reply-To header.
        if (!MailboxAddress.TryParse(email, out var mailbox) || !string.Equals(mailbox.Address, email, StringComparison.Ordinal))
            throw ValidationFailedException.For("email", "E-poçt ünvanı düzgün deyil.");
        // A name is one line wherever it goes (the subject, the Reply-To display name).
        var name = Spaces().Replace(req.Name.Trim(), " ");
        var message = req.Message.Replace("\r\n", "\n").Trim();

        var brand = await db.PlatformSettings.AsNoTracking().Select(s => s.PlatformName).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(brand)) brand = PlatformDefaults.PlatformName;

        try
        {
            await sender.SendAsync(EmailTemplates.ContactMessage(to, name, email, message, brand), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Contact message not sent: {ErrorType}", ex.GetType().Name);
            throw new AppException(StatusCodes.Status503ServiceUnavailable, NotSent);
        }
        logger.LogInformation("Contact message sent from {Sender}", EmailAddresses.Mask(email));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
