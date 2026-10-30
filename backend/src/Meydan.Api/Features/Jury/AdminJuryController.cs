using System.Text;
using Meydan.Api.Features.Admin;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Jury;

/// <summary>
/// The jury's evaluation of every work, from the administration's side (<see cref="JuryEvaluationService"/>): the roster,
/// the jurors and their invitations, progress, the score matrix, paper sheets, each juror's lock, the internal ranking
/// with its CSV, and the print data. Admins only; nothing here is public and nothing here publishes.
/// </summary>
[Route("api/admin/competitions/{competitionId:int}/jury-evaluation")]
[JuryNoStore]
public sealed class AdminJuryController(JuryEvaluationService jury, MeydanDbContext db) : AdminControllerBase
{
    [HttpGet]
    public Task<AdminJuryEvaluationDto> Get(int competitionId, CancellationToken ct) => jury.GetAsync(competitionId, ct);

    /// <summary>Starts the jury's evaluation of every work. The combined 50/50 final closes and stays as history.</summary>
    [HttpPost]
    public Task<AdminJuryEvaluationDto> Create(int competitionId, CancellationToken ct) => jury.CreateAsync(competitionId, CurrentUserId, ct);

    /// <summary>Freezes the works every juror scores: the ballot's works now. Refused once any score exists.</summary>
    [HttpPost("roster/freeze")]
    public Task<AdminJuryEvaluationDto> FreezeRoster(int competitionId, CancellationToken ct) =>
        jury.FreezeRosterAsync(competitionId, CurrentUserId, ct);

    [HttpPost("jurors")]
    public Task<AdminJuryEvaluationDto> AddJuror(int competitionId, JurorLabelRequest req, CancellationToken ct) =>
        jury.AddJurorAsync(competitionId, req.Label, CurrentUserId, ct);

    [HttpPut("jurors/{jurorId:int}")]
    public Task<AdminJuryEvaluationDto> RenameJuror(int competitionId, int jurorId, JurorLabelRequest req, CancellationToken ct) =>
        jury.RenameJurorAsync(competitionId, jurorId, req.Label, CurrentUserId, ct);

    [HttpDelete("jurors/{jurorId:int}")]
    public Task<AdminJuryEvaluationDto> RemoveJuror(int competitionId, int jurorId, CancellationToken ct) =>
        jury.RemoveJurorAsync(competitionId, jurorId, CurrentUserId, ct);

    /// <summary>
    /// A new personal invitation: the link (its secret in the #fragment) and a separate access code, shown this once and
    /// stored only as hashes. Any earlier invitation of the juror and every session of theirs end.
    /// </summary>
    [HttpPost("jurors/{jurorId:int}/invitation")]
    public Task<IssuedInvitationDto> IssueInvitation(int competitionId, int jurorId, IssueInvitationRequest req, CancellationToken ct) =>
        jury.IssueInvitationAsync(competitionId, jurorId, req.Days, CurrentUserId, ct);

    [HttpDelete("jurors/{jurorId:int}/invitation")]
    public Task<AdminJuryEvaluationDto> RevokeInvitation(int competitionId, int jurorId, CancellationToken ct) =>
        jury.RevokeInvitationAsync(competitionId, jurorId, CurrentUserId, ct);

    /// <summary>Completes a juror's evaluation on their behalf (e.g. a fully entered paper sheet).</summary>
    [HttpPost("jurors/{jurorId:int}/lock")]
    public Task<AdminJuryEvaluationDto> Lock(int competitionId, int jurorId, CancellationToken ct) =>
        jury.LockOnBehalfAsync(competitionId, jurorId, CurrentUserId, ct);

    /// <summary>Reopens one juror's scores for changes, with a mandatory reason. Audited.</summary>
    [HttpPost("jurors/{jurorId:int}/reopen")]
    public Task<AdminJuryEvaluationDto> Reopen(int competitionId, int jurorId, ReasonRequest req, CancellationToken ct) =>
        jury.ReopenAsync(competitionId, jurorId, req.Reason, CurrentUserId, ct);

    [HttpGet("matrix")]
    public Task<JuryMatrixDto> Matrix(int competitionId, CancellationToken ct) => jury.MatrixAsync(competitionId, ct);

    /// <summary>The score matrix as CSV (semicolon-separated, UTF-8 with BOM for spreadsheet programs).</summary>
    [HttpGet("export.csv")]
    public async Task<IActionResult> Export(int competitionId, CancellationToken ct)
    {
        var csv = await jury.ExportCsvAsync(competitionId, ct);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"jury-scores-{competitionId}.csv");
    }

    /// <summary>What a paper sheet would change — new, equal, conflicting, missing and invalid lines. Writes nothing.</summary>
    [HttpPost("paper/preview")]
    public Task<PaperPreviewDto> PaperPreview(int competitionId, PaperScoresRequest req, CancellationToken ct) =>
        jury.PreviewPaperAsync(competitionId, req, ct);

    /// <summary>Saves a paper sheet into the same score rows; conflicts need an explicit choice each and a reason.</summary>
    [HttpPost("paper/commit")]
    public Task<PaperCommitDto> PaperCommit(int competitionId, PaperScoresRequest req, CancellationToken ct) =>
        jury.CommitPaperAsync(competitionId, req, CurrentUserId, ct);

    /// <summary>
    /// Every work by the jury's exact average, 1 to N, with shared positions marked as ties — once every juror has
    /// submitted. Internal: for the administration only.
    /// </summary>
    [HttpGet("ranking")]
    public Task<JuryRankingDto> Ranking(int competitionId, CancellationToken ct) => jury.RankingAsync(competitionId, ct);

    /// <summary>The internal ranking as CSV (semicolon-separated, UTF-8 with BOM).</summary>
    [HttpGet("ranking.csv")]
    public async Task<IActionResult> RankingCsv(int competitionId, CancellationToken ct)
    {
        var csv = await jury.RankingCsvAsync(competitionId, ct);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"jury-ranking-{competitionId}.csv");
    }

    /// <summary>The frozen roster for the printed catalog and score sheets. No author, no vote.</summary>
    [HttpGet("print")]
    public Task<JuryPrintDto> Print(int competitionId, CancellationToken ct) => jury.PrintAsync(competitionId, ct);

    /// <summary>The evaluation's own audit timeline, newest first.</summary>
    [HttpGet("audit")]
    public async Task<IReadOnlyList<AuditEntryDto>> Audit(int competitionId, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var id = competitionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntities.Competition && a.EntityId == id && JuryActions.Contains(a.Action))
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Take(Math.Clamp(take, 1, 500))
            .Select(a => new AuditEntryDto(a.Id, a.Action, a.EntityType, a.EntityId, a.Summary,
                db.Users.Where(u => u.Id == a.UserId).Select(u => u.FirstName + " " + u.LastName).FirstOrDefault(),
                a.CreatedAt))
            .ToListAsync(ct);
    }

    private static readonly string[] JuryActions =
    [
        AuditActions.JuryEvaluationCreated, AuditActions.JuryRosterFrozen, AuditActions.JuryJurorAssigned,
        AuditActions.JuryJurorUnassigned, AuditActions.JuryInvitationIssued, AuditActions.JuryInvitationRevoked,
        AuditActions.JurorSignedIn, AuditActions.JurorSignInFailed, AuditActions.JurorIdentityConfirmed, AuditActions.JurorSignedOut,
        AuditActions.JuryWorkScored, AuditActions.JuryPaperScoresEntered, AuditActions.JuryEvaluationSubmitted,
        AuditActions.JuryEvaluationReopened, AuditActions.FinalJurorRenamed,
    ];
}
