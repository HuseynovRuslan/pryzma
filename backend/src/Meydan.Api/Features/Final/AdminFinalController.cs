using Meydan.Api.Features.Admin;
using Microsoft.AspNetCore.Mvc;

namespace Meydan.Api.Features.Final;

/// <summary>
/// The jury final of a competition (D13), for admins only: freeze the ten, the jury's marks, the lock, a tie decision,
/// the show's data, and the explicit publish. Nothing here is public until the publish step writes the official result.
/// </summary>
[Route("api/admin/competitions/{competitionId:int}/final")]
public sealed class AdminFinalController(FinalAdministration finals) : AdminControllerBase
{
    /// <summary>Where the final stands. Before the jury's lock it carries no votes and no public rank of a finalist.</summary>
    [HttpGet]
    public Task<AdminFinalDto> Get(int competitionId, CancellationToken ct) => finals.GetAsync(competitionId, ct);

    [HttpPost("freeze")]
    public Task<AdminFinalDto> Freeze(int competitionId, FreezeFinalRequest req, CancellationToken ct) =>
        finals.FreezeAsync(competitionId, req, CurrentUserId, ct);

    [HttpPost("jurors")]
    public Task<AdminFinalDto> AddJuror(int competitionId, CancellationToken ct) =>
        finals.AddJurorAsync(competitionId, CurrentUserId, ct);

    /// <summary>Gives a juror a name (e.g. their own) for the admin's screens and sheets. Never shown publicly. Audited.</summary>
    [HttpPut("jurors/{jurorId:int}")]
    public Task<AdminFinalDto> RenameJuror(int competitionId, int jurorId, RenameJurorRequest req, CancellationToken ct) =>
        finals.RenameJurorAsync(competitionId, jurorId, req, CurrentUserId, ct);

    [HttpDelete("jurors/{jurorId:int}")]
    public Task<AdminFinalDto> RemoveJuror(int competitionId, int jurorId, CancellationToken ct) =>
        finals.RemoveJurorAsync(competitionId, jurorId, CurrentUserId, ct);

    [HttpPut("marks")]
    public Task<AdminFinalDto> Marks(int competitionId, JuryMarksRequest req, CancellationToken ct) =>
        finals.SaveMarksAsync(competitionId, req, CurrentUserId, ct);

    [HttpPost("lock")]
    public Task<AdminFinalDto> Lock(int competitionId, CancellationToken ct) =>
        finals.LockAsync(competitionId, CurrentUserId, ct);

    [HttpPost("unlock")]
    public Task<AdminFinalDto> Unlock(int competitionId, UnlockJuryRequest req, CancellationToken ct) =>
        finals.UnlockAsync(competitionId, req, CurrentUserId, ct);

    [HttpPost("tie-breaks")]
    public Task<AdminFinalDto> TieBreak(int competitionId, TieBreakRequest req, CancellationToken ct) =>
        finals.RecordTieBreakAsync(competitionId, req, CurrentUserId, ct);

    /// <summary>The public vote's result as frozen for the final: the ten, from the snapshot. Read-only.</summary>
    [HttpGet("print/top10")]
    public Task<LeaderboardPrintDto> Top10Print(int competitionId, [FromQuery] bool includeAuthor, CancellationToken ct) =>
        finals.Top10PrintAsync(competitionId, includeAuthor, ct);

    /// <summary>Every work by valid votes, from the closed vote. Read-only; not a list of winners.</summary>
    [HttpGet("print/full")]
    public Task<LeaderboardPrintDto> FullPrint(int competitionId, [FromQuery] bool includeAuthor, CancellationToken ct) =>
        finals.FullPrintAsync(competitionId, includeAuthor, ct);

    /// <summary>The show's data. Read-only: running the show any number of times changes nothing.</summary>
    [HttpGet("show")]
    public Task<FinalShowDto> Show(int competitionId, CancellationToken ct) => finals.ShowAsync(competitionId, ct);

    [HttpPost("publish")]
    public Task<AdminFinalDto> Publish(int competitionId, PublishFinalRequest req, CancellationToken ct) =>
        finals.PublishAsync(competitionId, req, CurrentUserId, ct);
}
