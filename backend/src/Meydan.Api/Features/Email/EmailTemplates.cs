using System.Net;
using Meydan.Infrastructure.Email;

namespace Meydan.Api.Features.Email;

/// <summary>
/// Transactional e-mails in Azerbaijani: a small, table-free HTML body in the platform's palette plus a plain-text
/// alternative. Every dynamic value is HTML-encoded; links are built by the server from App:PublicUrl only.
/// <paramref name="brand"/> is the platform's name as the admin set it (PlatformSettings), read when the mail is sent.
/// </summary>
public static class EmailTemplates
{
    public const string PasswordResetKind = "password-reset";
    public const string SubmissionReceivedKind = "submission-received";
    public const string EmailConfirmationKind = "email-confirmation";
    public const string RegistrationVerifyKind = "registration-verify";
    public const string RegistrationExistsKind = "registration-exists";
    public const string ContactMessageKind = "contact-message";

    /// <summary>
    /// A message from the site's contact form, to the organiser. It comes from our own address; "Reply" goes to the
    /// person who wrote. Their words are shown as written — encoded, never as markup.
    /// </summary>
    public static EmailMessage ContactMessage(string to, string name, string email, string message, string brand)
    {
        var subject = $"{brand}: saytdan yeni mesaj — {name}";
        var text =
            "Saytın \"Əlaqə\" səhifəsindən yeni mesaj.\n\n" +
            $"Ad: {name}\nE-poçt: {email}\n\n" +
            $"{message}\n\n" +
            $"Cavab vermək üçün bu məktuba cavab yazın: cavab {email} ünvanına gedəcək.\n\n" +
            brand;
        var html = Layout(brand, subject,
            "<p style=\"font-size:20px;font-weight:bold;margin:0 0 12px\">Saytdan yeni mesaj</p>" +
            $"<p style=\"margin:0 0 4px\"><span style=\"color:#5b6b70\">Ad:</span> {H(name)}</p>" +
            $"<p style=\"margin:0 0 16px\"><span style=\"color:#5b6b70\">E-poçt:</span> <a href=\"mailto:{H(email)}\">{H(email)}</a></p>" +
            "<div style=\"margin:0 0 24px;padding:16px 20px;border-left:4px solid #10262B;background:#E3E5DF;white-space:pre-wrap\">" +
            H(message) + "</div>" +
            $"<p style=\"font-size:14px;color:#3e5157\">Cavab vermək üçün bu məktuba cavab yazın: cavab {H(email)} ünvanına gedəcək.</p>");
        return new EmailMessage(to, subject, html, text, ContactMessageKind, ReplyTo: email, ReplyToName: name);
    }

    /// <summary>Email-first registration: the link that proves the address before any account exists.</summary>
    public static EmailMessage RegistrationVerify(string to, string link, int validMinutes, string brand)
    {
        var subject = $"{brand}: e-mailinizi təsdiqləyin";
        var text =
            "E-mailinizi təsdiqləyin.\n\n" +
            "Qeydiyyatı davam etdirmək üçün e-mail ünvanınızı təsdiqləyin. Bu linki açın:\n" +
            $"{link}\n\n" +
            $"Link {validMinutes} dəqiqə etibarlıdır və yalnız bir dəfə işləyir.\n" +
            "Qeydiyyatı siz başlamamısınızsa, məktubu nəzərə almayın — heç bir hesab yaradılmayıb.\n\n" +
            brand;
        var html = Layout(brand, subject,
            "<p style=\"font-size:20px;font-weight:bold;margin:0 0 12px\">E-mailinizi təsdiqləyin</p>" +
            "<p>Qeydiyyatı davam etdirmək üçün e-mail ünvanınızı təsdiqləyin.</p>" +
            Button(link, "Qeydiyyata davam et") +
            $"<p style=\"font-size:14px;color:#3e5157\">Link {validMinutes} dəqiqə etibarlıdır və yalnız bir dəfə işləyir. " +
            "Düymə işləmirsə, bu ünvanı brauzerə köçürün:<br><span style=\"word-break:break-all\">" + H(link) + "</span></p>" +
            "<p style=\"font-size:14px;color:#3e5157\">Qeydiyyatı siz başlamamısınızsa, məktubu nəzərə almayın — heç bir hesab yaradılmayıb.</p>");
        return new EmailMessage(to, subject, html, text, RegistrationVerifyKind);
    }

    /// <summary>Someone tried to register with an address that already has an account: tell its owner, not the form.</summary>
    public static EmailMessage RegistrationExists(string to, string firstName, string loginLink, string resetLink, string brand)
    {
        var subject = $"{brand}: bu e-mail ilə hesabınız artıq var";
        var text =
            $"Salam, {firstName}!\n\n" +
            $"Kimsə bu e-mail ilə {brand}-da yeni qeydiyyat başlatmaq istədi. Bu ünvanla hesabınız artıq var.\n" +
            $"Daxil olun: {loginLink}\nŞifrəni unutmusunuzsa: {resetLink}\n\n" +
            "Bunu siz etməmisinizsə, məktubu nəzərə almayın — hesabınızda heç nə dəyişməyib.\n\n" +
            brand;
        var html = Layout(brand, subject,
            $"<p>Salam, {H(firstName)}!</p>" +
            $"<p>Kimsə bu e-mail ilə {H(brand)}-da yeni qeydiyyat başlatmaq istədi. Bu ünvanla hesabınız artıq var.</p>" +
            Button(loginLink, "Daxil ol") +
            $"<p style=\"font-size:14px;color:#3e5157\">Şifrəni unutmusunuzsa: <a href=\"{H(resetLink)}\">şifrəni bərpa edin</a>.</p>" +
            "<p style=\"font-size:14px;color:#3e5157\">Bunu siz etməmisinizsə, məktubu nəzərə almayın — hesabınızda heç nə dəyişməyib.</p>");
        return new EmailMessage(to, subject, html, text, RegistrationExistsKind);
    }

    public static EmailMessage EmailConfirmation(string to, string firstName, string link, int validHours, string brand)
    {
        var subject = $"{brand}: e-mail ünvanınızı təsdiqləyin";
        var text =
            $"Salam, {firstName}!\n\n" +
            "Səs vermək üçün e-mail ünvanınızı təsdiqləyin. Bu linki açın:\n" +
            $"{link}\n\n" +
            $"Link {validHours} saat etibarlıdır.\n" +
            "Hesabı siz açmamısınızsa, məktubu nəzərə almayın.\n\n" +
            brand;
        var html = Layout(brand, subject,
            $"<p>Salam, {H(firstName)}!</p>" +
            "<p>Səs vermək üçün e-mail ünvanınızı təsdiqləyin.</p>" +
            Button(link, "E-maili təsdiqlə") +
            $"<p style=\"font-size:14px;color:#3e5157\">Link {validHours} saat etibarlıdır. " +
            "Düymə işləmirsə, bu ünvanı brauzerə köçürün:<br><span style=\"word-break:break-all\">" + H(link) + "</span></p>" +
            "<p style=\"font-size:14px;color:#3e5157\">Hesabı siz açmamısınızsa, məktubu nəzərə almayın.</p>");
        return new EmailMessage(to, subject, html, text, EmailConfirmationKind);
    }

    public static EmailMessage PasswordReset(string to, string firstName, string link, int validMinutes, string brand)
    {
        var subject = $"{brand}: şifrənin yenilənməsi";
        var text =
            $"Salam, {firstName}!\n\n" +
            $"{brand} hesabınız üçün şifrənin yenilənməsi istənildi. Yeni şifrə təyin etmək üçün bu linki açın:\n" +
            $"{link}\n\n" +
            $"Link {validMinutes} dəqiqə etibarlıdır və yalnız bir dəfə işləyir.\n" +
            "Bu sorğunu siz göndərməmisinizsə, məktubu nəzərə almayın — şifrəniz dəyişməyəcək.\n\n" +
            brand;
        var html = Layout(brand, subject,
            $"<p>Salam, {H(firstName)}!</p>" +
            $"<p>{H(brand)} hesabınız üçün şifrənin yenilənməsi istənildi. Yeni şifrə təyin etmək üçün düyməni basın:</p>" +
            Button(link, "Yeni şifrə təyin et") +
            $"<p style=\"font-size:14px;color:#3e5157\">Link {validMinutes} dəqiqə etibarlıdır və yalnız bir dəfə işləyir. " +
            "Düymə işləmirsə, bu ünvanı brauzerə köçürün:<br><span style=\"word-break:break-all\">" + H(link) + "</span></p>" +
            "<p style=\"font-size:14px;color:#3e5157\">Bu sorğunu siz göndərməmisinizsə, məktubu nəzərə almayın — şifrəniz dəyişməyəcək.</p>");
        return new EmailMessage(to, subject, html, text, PasswordResetKind);
    }

    public static EmailMessage SubmissionReceived(
        string to, string firstName, string reference, string competitionTitle, string? categoryName, string link,
        string brand)
    {
        var subject = $"Təklifiniz qəbul edildi — {reference}";
        var category = categoryName is null ? "" : $"\n{categoryName}";
        var text =
            $"Salam, {firstName}!\n\n" +
            $"Təklifiniz qəbul edildi.\nİstinad nömrəsi: {reference}\n{competitionTitle}{category}\n\n" +
            $"Təklifinizə baxmaq üçün: {link}\n\n" +
            $"Bu məktub avtomatik göndərilib. {brand}";
        var html = Layout(brand, subject,
            $"<p>Salam, {H(firstName)}!</p>" +
            "<p>Təklifiniz qəbul edildi.</p>" +
            "<p style=\"margin:24px 0;padding:16px 20px;border-left:4px solid #10262B;background:#E3E5DF\">" +
            "<span style=\"font-size:13px;color:#5b6b70\">İstinad nömrəsi</span><br>" +
            $"<strong style=\"font-size:20px;letter-spacing:0.02em\">{H(reference)}</strong><br>" +
            $"<span>{H(competitionTitle)}</span>" +
            (categoryName is null ? "" : $"<br><span>{H(categoryName)}</span>") +
            "</p>" +
            Button(link, "Təklifə bax") +
            "<p style=\"font-size:14px;color:#3e5157\">Bu məktub avtomatik göndərilib.</p>");
        return new EmailMessage(to, subject, html, text, SubmissionReceivedKind);
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);

    private static string Button(string href, string label) =>
        $"<p style=\"margin:28px 0\"><a href=\"{H(href)}\" style=\"display:inline-block;padding:14px 22px;" +
        "background:#10262B;color:#F3F4F0;text-decoration:none;font-weight:bold\">" + H(label) + "</a></p>";

    private static string Layout(string brand, string title, string content) =>
        "<!doctype html><html lang=\"az\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width\">" +
        $"<title>{H(title)}</title></head>" +
        "<body style=\"margin:0;padding:0;background:#F3F4F0;color:#10262B;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:1.5\">" +
        "<div style=\"max-width:560px;margin:0 auto;padding:32px 24px\">" +
        "<p style=\"margin:0 0 24px;font-weight:bold;letter-spacing:0.12em\">" +
        "<span style=\"display:inline-block;width:10px;height:10px;background:#F0B323;transform:rotate(45deg);margin-right:10px\"></span>" + H(brand) + "</p>" +
        "<div style=\"border-top:2px solid #10262B;padding-top:24px\">" + content + "</div>" +
        $"<p style=\"margin-top:32px;font-size:12px;color:#5b6b70\">{H(brand)} — yaradıcı layihələr və açıq müsabiqələr üçün platforma.</p>" +
        "</div></body></html>";
}
