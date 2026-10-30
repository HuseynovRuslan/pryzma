using System.Text;
using Meydan.Api.Features.Auth;
using Meydan.Domain.Entities;
using Meydan.Infrastructure;
using Meydan.Infrastructure.Email;
using Meydan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Email;

/// <summary>
/// Turns an outbox row (kind + user id + subject id) into a message at delivery time. The password-reset token is
/// minted here, immediately before sending, so it never exists in the database — only in memory and in the e-mail.
/// Recipient address and names are read now as well, so a changed e-mail address is respected.
/// </summary>
public sealed class EmailComposer(
    UserManager<ApplicationUser> users, MeydanDbContext db, IConfiguration config,
    Meydan.Api.Features.Auth.EmailFirstRegistrationPolicy registration, TimeProvider clock) : IEmailComposer
{
    public async Task<EmailMessage?> ComposeAsync(EmailOutboxMessage m, CancellationToken ct)
    {
        // The name the site shows (admin-editable), so a renamed platform signs its mail the same way.
        var brand = await db.PlatformSettings.AsNoTracking().Select(s => s.PlatformName).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(brand)) brand = PlatformDefaults.PlatformName;

        // A registration with no account yet: the link is minted now and exists only in this mail.
        if (m.PendingRegistrationId is { } pendingId)
        {
            if (m.Kind != EmailTemplates.RegistrationVerifyKind) return null;
            var minted = await Meydan.Api.Features.Auth.EmailFirstRegistration.MintLinkAsync(db, registration, clock, pendingId, ct);
            if (minted is not { } link) return null;
            var url = $"{AuthService.PublicUrl(config)}/register/verify#token={link.Token}";
            return EmailTemplates.RegistrationVerify(link.Email, url, (int)link.ValidFor.TotalMinutes, brand);
        }

        if (m.UserId is not { } userId) return null;
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || string.IsNullOrEmpty(user.Email)) return null;

        switch (m.Kind)
        {
            case EmailTemplates.PasswordResetKind:
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
                // Fragment, not query string: browsers never send "#…" to a server, so the token stays out of access
                // logs and Referer headers. The host comes from configuration, never from a request.
                var link = $"{AuthService.PublicUrl(config)}/reset-password#email={Uri.EscapeDataString(user.Email)}&token={encoded}";
                var minutes = Math.Max(1, (int)Math.Round(DependencyInjection.PasswordResetLifetime(config).TotalMinutes));
                return EmailTemplates.PasswordReset(user.Email, user.FirstName, link, minutes, brand);
            }
            case EmailTemplates.EmailConfirmationKind:
            {
                // Confirmed in the meantime (an earlier link): nothing to send.
                if (user.EmailConfirmed) return null;
                var token = await users.GenerateEmailConfirmationTokenAsync(user);
                var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
                // A fragment for the same reason as the reset link: it never reaches a server log.
                var link = $"{AuthService.PublicUrl(config)}/confirm-email#email={Uri.EscapeDataString(user.Email)}&token={encoded}";
                var hours = Math.Max(1, (int)Math.Round(DependencyInjection.EmailConfirmationLifetime(config).TotalHours));
                return EmailTemplates.EmailConfirmation(user.Email, user.FirstName, link, hours, brand);
            }
            case EmailTemplates.RegistrationExistsKind:
                return EmailTemplates.RegistrationExists(user.Email, user.FirstName,
                    $"{AuthService.PublicUrl(config)}/login", $"{AuthService.PublicUrl(config)}/forgot-password", brand);
            case EmailTemplates.SubmissionReceivedKind:
            {
                var s = await db.Submissions.AsNoTracking()
                    .Where(x => x.Id == m.SubjectId && x.UserId == user.Id && x.ReferenceNumber != null)
                    .Select(x => new { x.Id, x.ReferenceNumber, Competition = x.Competition.Title, Category = x.Category != null ? x.Category.Name : null })
                    .FirstOrDefaultAsync(ct);
                if (s is null) return null;
                return EmailTemplates.SubmissionReceived(user.Email, user.FirstName, s.ReferenceNumber!, s.Competition, s.Category,
                    $"{AuthService.PublicUrl(config)}/submissions/{s.Id}", brand);
            }
            default:
                return null;
        }
    }
}
