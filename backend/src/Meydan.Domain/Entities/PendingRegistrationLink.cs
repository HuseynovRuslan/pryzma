namespace Meydan.Domain.Entities;

/// <summary>
/// One link sent for a <see cref="PendingRegistration"/>: the SHA-256 of the token in that mail and when it stops
/// working. Every mail carries its own link, and asking for a new one does not cancel the ones before it (the owner,
/// 21 October 2026): people opened an older mail of the same thread and were told the link was invalid, asked again,
/// and ran into the daily limit. Each link works once; all of them go with the registration.
/// </summary>
public class PendingRegistrationLink
{
    public long Id { get; set; }
    public Guid PendingRegistrationId { get; set; }

    /// <summary>SHA-256 (hex) of the link's token; the token itself exists only in the mail.</summary>
    public string TokenHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
