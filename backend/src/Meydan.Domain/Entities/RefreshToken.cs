namespace Meydan.Domain.Entities;

/// <summary>Rotating refresh token. Only the SHA-256 hash of the token is stored.</summary>
public class RefreshToken
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    /// <summary>All tokens minted from one login share a family, so reuse revokes the whole chain.</summary>
    public Guid FamilyId { get; set; }
    public bool IsPersistent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
    public string? CreatedByIp { get; set; }

    /// <summary>
    /// The browser's User-Agent at sign-in or refresh, cut to 512 characters. Fraud evidence only (D20): with the IP
    /// and <see cref="CreatedByDeviceId"/> it shows one phone cycling several accounts. Never published.
    /// </summary>
    public string? CreatedByUserAgent { get; set; }

    /// <summary>
    /// The random id the site's own frontend keeps in this browser (header X-Device-Id); null when absent or
    /// malformed. It identifies a browser, not a person, and is read only by an audit (D20).
    /// </summary>
    public string? CreatedByDeviceId { get; set; }

    public ApplicationUser User { get; set; } = null!;

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>Per-prefix, per-year counter used to mint reference numbers like SUB-2026-00124.</summary>
public class ReferenceCounter
{
    public string Prefix { get; set; } = "";
    public int Year { get; set; }
    public int Value { get; set; }
}
