namespace Meydan.Domain.Entities;

public enum EmailOutboxStatus
{
    /// <summary>Waiting for (another) delivery attempt.</summary>
    Pending = 0,
    Sent = 1,
    /// <summary>Retries exhausted.</summary>
    Failed = 2,
    /// <summary>Not delivered before it stopped being useful (e.g. a password-reset request older than a token's lifetime).</summary>
    Expired = 3,
    /// <summary>Nothing to send any more (account or submission gone).</summary>
    Cancelled = 4,
}

/// <summary>
/// A durable e-mail job in PostgreSQL. It stores WHAT to send and to WHOM by reference only — a kind, a user id and
/// optionally a subject id (e.g. the submission) — never an address, a rendered body, a password-reset token or any
/// other secret. The message is composed at delivery time (a reset token is minted only then), so a pending row is
/// harmless if read, and survives restarts.
/// </summary>
public class EmailOutboxMessage
{
    public long Id { get; set; }
    /// <summary>Template identifier, e.g. "password-reset", "submission-received".</summary>
    public string Kind { get; set; } = "";
    /// <summary>The account the mail is for; null only for a mail to a pending registration.</summary>
    public Guid? UserId { get; set; }
    /// <summary>The email-first registration the mail is for (its verification link); null for every other mail.</summary>
    public Guid? PendingRegistrationId { get; set; }
    public int? SubjectId { get; set; }
    public EmailOutboxStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    /// <summary>After this the message is dropped as Expired instead of sent.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public int AttemptCount { get; set; }
    /// <summary>Lease: while in the future, the row belongs to <see cref="LockedBy"/> (one worker instance).</summary>
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    /// <summary>Exception type / SMTP status code of the last failure — never message text or content.</summary>
    public string? LastError { get; set; }
}
