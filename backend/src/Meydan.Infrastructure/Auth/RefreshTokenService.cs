using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meydan.Infrastructure.Auth;

/// <summary>The raw token goes to the client exactly once; the database only sees its hash.</summary>
public sealed record IssuedRefreshToken(string RawToken, DateTimeOffset ExpiresAt, bool IsPersistent);

public sealed record RotationResult(ApplicationUser User, IssuedRefreshToken Token);

/// <summary>What a presented refresh cookie looks like, decided without any database work.</summary>
public enum RefreshTokenShape
{
    Missing,
    /// <summary>Nothing ever issued looks like this.</summary>
    Malformed,
    /// <summary>"&lt;family&gt;.&lt;secret&gt;" — every token issued since the family was added to the cookie.</summary>
    Current,
    /// <summary>The bare secret, as issued before 2026-09-19; it rotates into the current form on its next use.</summary>
    Legacy,
}

public sealed partial class RefreshTokenService(
    MeydanDbContext db,
    IOptions<JwtOptions> options,
    TimeProvider clock,
    ILogger<RefreshTokenService> logger)
{
    private static readonly TimeSpan ReuseGrace = TimeSpan.FromSeconds(30);

    // A token is "<family>.<secret>": the family is the session's id (32 hex, stable across rotations, not a secret)
    // so throttling can tell sessions apart before any lookup; the secret (48 random bytes) is what authenticates.
    // The whole string is hashed for storage, so neither part can be swapped for another session's. \z, not $:
    // cookie values are URL-decoded, and $ would also accept a trailing (encoded) newline.
    [GeneratedRegex("^[0-9a-f]{32}\\.[A-Za-z0-9_-]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CurrentForm();

    [GeneratedRegex("^[A-Za-z0-9_-]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyForm();

    public static RefreshTokenShape Classify(string? raw) =>
        string.IsNullOrEmpty(raw) ? RefreshTokenShape.Missing
        : CurrentForm().IsMatch(raw) ? RefreshTokenShape.Current
        : LegacyForm().IsMatch(raw) ? RefreshTokenShape.Legacy
        : RefreshTokenShape.Malformed;

    /// <summary>The session (family) part of a current-form token; false for any other shape.</summary>
    public static bool TryGetFamilyKey(string? raw, out string familyKey)
    {
        familyKey = Classify(raw) == RefreshTokenShape.Current ? raw![..32] : "";
        return familyKey.Length > 0;
    }

    public async Task<IssuedRefreshToken> IssueAsync(
        Guid userId, bool persistent, string? ip, CancellationToken ct, Guid? familyId = null)
    {
        var now = clock.GetUtcNow();
        var family = familyId ?? Guid.NewGuid();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var raw = $"{family:N}.{secret}";
        var expires = persistent
            ? now.AddDays(options.Value.RefreshTokenDaysPersistent)
            : now.AddHours(options.Value.RefreshTokenHoursSession);

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(raw),
            FamilyId = family,
            IsPersistent = persistent,
            CreatedAt = now,
            ExpiresAt = expires,
            CreatedByIp = ip,
        });
        await db.SaveChangesAsync(ct);
        return new IssuedRefreshToken(raw, expires, persistent);
    }

    /// <summary>
    /// Exchanges a valid token for a new one. Presenting an already-rotated token is treated as theft:
    /// the whole family is revoked and the caller must log in again.
    /// </summary>
    public async Task<RotationResult?> RotateAsync(string rawToken, string? ip, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = Hash(rawToken);
        var existing = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (existing is null)
            return null;

        if (existing.RevokedAt is not null)
        {
            // Two tabs refreshing at the same moment both present the same token. Within a short
            // grace window that is a race, not theft: mint a sibling in the same family instead.
            if (existing.ReplacedByHash is not null && now - existing.RevokedAt <= ReuseGrace
                && existing.ExpiresAt > now)
            {
                var sibling = await IssueAsync(existing.UserId, existing.IsPersistent, ip, ct, existing.FamilyId);
                return new RotationResult(existing.User, sibling);
            }
            if (existing.ReplacedByHash is not null)
            {
                logger.LogWarning("Refresh token reuse detected for user {UserId}; revoking family", existing.UserId);
                await RevokeFamilyAsync(existing.FamilyId, ct);
            }
            return null;
        }
        if (existing.ExpiresAt <= now)
            return null;

        var issued = await IssueAsync(existing.UserId, existing.IsPersistent, ip, ct, existing.FamilyId);
        existing.RevokedAt = now;
        existing.ReplacedByHash = Hash(issued.RawToken);
        await db.SaveChangesAsync(ct);
        return new RotationResult(existing.User, issued);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct)
    {
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is { RevokedAt: null })
        {
            token.RevokedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in active) t.RevokedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var family = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in family) t.RevokedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
