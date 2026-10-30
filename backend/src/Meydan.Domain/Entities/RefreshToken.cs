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
