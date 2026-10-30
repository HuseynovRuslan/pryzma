namespace Meydan.Domain.Entities;

/// <summary>
/// Records content seed steps that already ran, so each runs exactly once per database: later admin edits
/// are never overwritten and deleted rows are never resurrected. Infrastructure bookkeeping, not business data.
/// </summary>
public class SeedHistory
{
    public string Key { get; set; } = "";
    public DateTimeOffset AppliedAt { get; set; }
}
