namespace Meydan.Domain.Entities;

/// <summary>
/// A registration that has an e-mail address and nothing else yet (email-first registration). No account exists
/// until the address is proven: the person asks for a link, opens it, and only then gives their name, phone and
/// password, at which point the account is created already confirmed and this row is deleted.
/// <para>
/// No secret is stored in the clear. Each link's token exists only in its e-mail (it is minted when the mail is
/// composed); <see cref="Links"/> keep their SHA-256. Opening a link spends that token and issues a short-lived proof,
/// also kept only as a hash, which the completion form must present. One row per address: asking again adds a link
/// and leaves the earlier ones working until they run out, and the unique index stops two registrations for one
/// address from running side by side.
/// </para>
/// </summary>
public class PendingRegistration
{
    public Guid Id { get; set; }

    /// <summary>The address as the person typed it (trimmed) — the account's e-mail once it is created.</summary>
    public string Email { get; set; } = "";

    /// <summary>The address as Identity normalizes it; one pending registration per address.</summary>
    public string NormalizedEmail { get; set; } = "";

    /// <summary>The links sent so far that are not yet used; expired ones go with the registration.</summary>
    public ICollection<PendingRegistrationLink> Links { get; set; } = [];

    /// <summary>When a link was last opened.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>SHA-256 (hex) of the proof handed to the completion form; null until the link is opened.</summary>
    public string? ProofHash { get; set; }
    public DateTimeOffset? ProofExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The row is swept after this; every new link moves it on.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Links asked for in the window that started at <see cref="SendWindowStartedAt"/> (rate limit).</summary>
    public int SendCount { get; set; }
    public DateTimeOffset SendWindowStartedAt { get; set; }
    public DateTimeOffset LastSentAt { get; set; }
}
