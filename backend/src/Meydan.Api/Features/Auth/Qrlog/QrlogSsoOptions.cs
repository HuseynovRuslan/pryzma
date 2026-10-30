namespace Meydan.Api.Features.Auth.Qrlog;

/// <summary>
/// "QRLog ilə daxil ol" (configuration section "QrlogSso"). QRLog is not an OpenID provider; it vouches for a signed-in
/// employee over a back channel, the way it already does for Kitabxana 2.0: this site starts a ticket, sends the person
/// to QRLog with the ticket's code, the person approves there, and QRLog's server posts the identity to
/// /api/auth/qrlog/confirm signed with the secret the two systems share. Off until every value is set.
/// </summary>
public sealed class QrlogSsoOptions
{
    public const string Section = "QrlogSso";
    public const string Provider = "QRLog";

    public bool Enabled { get; set; }

    /// <summary>Shared with QRLog (ExternalSignIn:Apps:meydan:VouchSecret there). Anyone holding it can claim to be any employee.</summary>
    public string VouchSecret { get; set; } = "";

    /// <summary>
    /// Where the person approves the sign-in, with <c>{code}</c> and <c>{return}</c> placeholders,
    /// e.g. https://app.qrlog.az/signin/meydan?code={code}&amp;return={return}. Must be https on qrlog.az.
    /// </summary>
    public string AuthorizeUrl { get; set; } = "";

    /// <summary>A ticket is meant to be used now: long enough to unlock a phone and tap once, short enough not to be carried away.</summary>
    public int TicketMinutes { get; set; } = 5;

    /// <summary>How far QRLog's clock may drift from ours before a confirmation is refused as a replay.</summary>
    public int SignatureWindowMinutes { get; set; } = 5;

    public static bool IsValidAuthorizeUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains("{code}", StringComparison.Ordinal)
        && value.Contains("{return}", StringComparison.Ordinal)
        && Uri.TryCreate(value.Replace("{code}", "x", StringComparison.Ordinal).Replace("{return}", "x", StringComparison.Ordinal),
            UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("qrlog.az", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".qrlog.az", StringComparison.OrdinalIgnoreCase));
}
