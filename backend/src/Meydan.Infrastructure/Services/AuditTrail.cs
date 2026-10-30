using Meydan.Domain.Entities;
using Meydan.Infrastructure.Persistence;

namespace Meydan.Infrastructure.Services;

/// <summary>
/// Adds audit records to the current unit of work, so an action and its audit entry are saved together
/// (or not at all). Callers pass identifiers and a short neutral summary — never secrets or personal data.
/// </summary>
public sealed class AuditTrail(MeydanDbContext db, TimeProvider clock)
{
    public void Record(Guid? userId, string action, string entityType, object entityId, string? summary = null) =>
        db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = Convert.ToString(entityId, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Summary = summary is { Length: > 500 } ? summary[..500] : summary,
            CreatedAt = clock.GetUtcNow(),
        });
}

public static class AuditEntities
{
    public const string Project = "Project";
    public const string Competition = "Competition";
    public const string Category = "Category";
    public const string Field = "Field";
    public const string Submission = "Submission";
    /// <summary>A user's own account (linked sign-in methods); the entity id is the user id.</summary>
    public const string Account = "Account";
    /// <summary>The platform as a whole (demo data); the entity id names the step, e.g. "demo-data-v1".</summary>
    public const string Platform = "Platform";
    /// <summary>One vote; the entity id is the vote's id. Never names the voter.</summary>
    public const string Vote = "Vote";
}
