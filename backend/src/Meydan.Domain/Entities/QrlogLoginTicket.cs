namespace Meydan.Domain.Entities;

public enum QrlogTicketPurpose
{
    /// <summary>Sign in (creating the MEYDAN account on first use).</summary>
    Login = 0,
    /// <summary>Attach a QRLog identity to the already signed-in MEYDAN account.</summary>
    Link = 1,
}

public enum QrlogTicketStatus
{
    /// <summary>Started in a browser; waiting for QRLog's server to vouch for somebody.</summary>
    Pending = 0,
    /// <summary>QRLog confirmed an identity; the browser that started it has not collected the result yet.</summary>
    Confirmed = 1,
    /// <summary>The result was collected exactly once; the identity fields are cleared.</summary>
    Claimed = 2,
    /// <summary>Cancelled by the browser that started it.</summary>
    Ended = 3,
}

/// <summary>
/// One "QRLog ilə daxil ol" attempt. The browser gets the random <see cref="Code"/> (it travels to QRLog in the
/// address) and a separate claim secret in an HttpOnly cookie (it never leaves this site); only the two together
/// collect the result. QRLog's server fills in the identity over a signed back channel — the browser is never
/// handed a token. Rows are short-lived: minutes to live, identity cleared on claim, deleted a day later.
/// </summary>
public class QrlogLoginTicket
{
    public Guid Id { get; set; }
    /// <summary>16 random bytes as lower-case hex; what QRLog confirms.</summary>
    public string Code { get; set; } = "";
    /// <summary>SHA-256 (base64url) of the cookie secret; the secret itself is never stored.</summary>
    public string ClaimSecretHash { get; set; } = "";
    public QrlogTicketPurpose Purpose { get; set; }
    /// <summary>For <see cref="QrlogTicketPurpose.Link"/>: the account that started it and the only one that may finish it.</summary>
    public Guid? LinkUserId { get; set; }
    /// <summary>Same-site path to continue at after signing in ("/" when nothing safe was given).</summary>
    public string ReturnTo { get; set; } = "/";
    public QrlogTicketStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }

    // Filled by QRLog's confirmation, cleared when the browser collects it.
    /// <summary>QRLog's stable identifier for the person (their employee id); never a MEYDAN key.</summary>
    public string? Subject { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
}
