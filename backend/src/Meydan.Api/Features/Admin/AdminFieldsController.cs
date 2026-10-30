using System.Text.Json;
using System.Text.RegularExpressions;
using Meydan.Api.Common;
using Meydan.Domain.Common;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Admin;

/// <summary>
/// Builds a competition's submission form without code. Historical answers stay intact: a field that has
/// answers keeps its key and type and can only be disabled, and submitted answers carry the label they were
/// given under.
/// </summary>
[Route("api/admin/competitions/{competitionId:int}/fields")]
public sealed partial class AdminFieldsController(MeydanDbContext db, AuditTrail audit, IOptions<UploadOptions> uploads)
    : AdminControllerBase
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,59}$")]
    private static partial Regex KeyPattern();

    [HttpGet]
    public async Task<IReadOnlyList<AdminFieldDto>> List(int competitionId, CancellationToken ct)
    {
        await EnsureCompetitionAsync(competitionId, ct);
        return await Query(db.CompetitionFields.Where(f => f.CompetitionId == competitionId)).ToListAsync(ct);
    }

    [HttpPost]
    public async Task<ActionResult<AdminFieldDto>> Create(int competitionId, FieldUpsertRequest req, CancellationToken ct)
    {
        await EnsureCompetitionAsync(competitionId, ct);
        var nextOrder = await db.CompetitionFields.Where(f => f.CompetitionId == competitionId)
            .MaxAsync(f => (int?)f.SortOrder, ct) ?? 0;
        var field = new CompetitionField { CompetitionId = competitionId, SortOrder = nextOrder + 1 };
        await ApplyAsync(field, req, locked: false, ct);
        db.CompetitionFields.Add(field);
        await db.SaveChangesAsync(ct);
        audit.Record(CurrentUserId, AuditActions.Created, AuditEntities.Field, field.Id, $"competition {competitionId}: {field.Key}");
        await db.SaveChangesAsync(ct);
        return Created($"/api/admin/competitions/{competitionId}/fields/{field.Id}", await GetAsync(competitionId, field.Id, ct));
    }

    [HttpPut("{fieldId:int}")]
    public async Task<AdminFieldDto> Update(int competitionId, int fieldId, FieldUpsertRequest req, CancellationToken ct)
    {
        var field = await FindAsync(competitionId, fieldId, ct);
        var locked = await HasDataAsync(field.Id, ct);
        await ApplyAsync(field, req, locked, ct);
        audit.Record(CurrentUserId, AuditActions.Updated, AuditEntities.Field, field.Id, $"competition {competitionId}: {field.Key}");
        await db.SaveChangesAsync(ct);
        return await GetAsync(competitionId, fieldId, ct);
    }

    /// <summary>Only a field nobody has answered can be deleted; otherwise disable it.</summary>
    [HttpDelete("{fieldId:int}")]
    public async Task<IActionResult> Delete(int competitionId, int fieldId, CancellationToken ct)
    {
        var field = await FindAsync(competitionId, fieldId, ct);
        if (await HasDataAsync(field.Id, ct))
            throw new ConflictException("Bu sahəyə artıq cavab verilib; silmək olmaz. Onu deaktiv edin.");
        // Drafts may hold empty placeholder rows for the field; they carry no data.
        db.SubmissionAnswers.RemoveRange(db.SubmissionAnswers.Where(a => a.CompetitionFieldId == fieldId));
        db.CompetitionFields.Remove(field);
        audit.Record(CurrentUserId, AuditActions.Deleted, AuditEntities.Field, fieldId, $"competition {competitionId}: {field.Key}");
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPut("order")]
    public async Task<IReadOnlyList<AdminFieldDto>> Reorder(int competitionId, ReorderRequest req, CancellationToken ct)
    {
        var fields = await db.CompetitionFields.Where(f => f.CompetitionId == competitionId).ToListAsync(ct);
        if (fields.Count == 0)
            await EnsureCompetitionAsync(competitionId, ct);
        if (req.Ids.Count != fields.Count || req.Ids.Distinct().Count() != fields.Count || req.Ids.Any(i => fields.All(f => f.Id != i)))
            throw ValidationFailedException.For("ids", "Sıralama formanın bütün sahələrini bir dəfə əhatə etməlidir.");
        for (var i = 0; i < req.Ids.Count; i++)
            fields.First(f => f.Id == req.Ids[i]).SortOrder = i + 1;
        audit.Record(CurrentUserId, AuditActions.FieldsReordered, AuditEntities.Competition, competitionId);
        await db.SaveChangesAsync(ct);
        return await Query(db.CompetitionFields.Where(f => f.CompetitionId == competitionId)).ToListAsync(ct);
    }

    /// <summary>Filter first, project last — predicates over the DTO record cannot be translated.</summary>
    private IQueryable<AdminFieldDto> Query(IQueryable<CompetitionField> source) =>
        source.AsNoTracking()
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
            .Select(f => new AdminFieldDto(f.Id, f.CompetitionId, f.Key, f.Label, f.FieldType, f.Placeholder, f.HelpText,
                f.IsRequired, f.IsSubmissionTitle, f.IsActive, f.IsPublicAfterResults, f.OptionsJson, f.SortOrder,
                db.SubmissionAnswers.Count(a => a.CompetitionFieldId == f.Id && a.Value != null)
                + db.SubmissionFiles.Count(x => x.CompetitionFieldId == f.Id)));

    private async Task<AdminFieldDto> GetAsync(int competitionId, int fieldId, CancellationToken ct) =>
        await Query(db.CompetitionFields.Where(f => f.CompetitionId == competitionId && f.Id == fieldId)).FirstAsync(ct);

    private async Task ApplyAsync(CompetitionField f, FieldUpsertRequest req, bool locked, CancellationToken ct)
    {
        if (!Enum.IsDefined(req.FieldType))
            throw ValidationFailedException.For("fieldType", "Sahə növü düzgün deyil.");

        var key = string.IsNullOrWhiteSpace(req.Key) ? Slug.From(req.Label).Replace('-', '_') : req.Key.Trim();
        if (key.Length == 0)
            key = $"field_{Guid.NewGuid():N}"[..14];
        if (!KeyPattern().IsMatch(key))
            throw ValidationFailedException.For("key", "Açar kiçik latın hərfləri, rəqəm, “-” və “_” ilə yazılmalıdır.");

        if (locked)
        {
            // Answers are addressed by this field's key and type; changing them would orphan history.
            if (req.FieldType != f.FieldType)
                throw ValidationFailedException.For("fieldType", "Bu sahəyə artıq cavab verilib; növünü dəyişmək olmaz.");
            if (!string.IsNullOrWhiteSpace(req.Key) && key != f.Key)
                throw ValidationFailedException.For("key", "Bu sahəyə artıq cavab verilib; açarını dəyişmək olmaz.");
            key = f.Key;
        }
        if (await db.CompetitionFields.AnyAsync(x => x.CompetitionId == f.CompetitionId && x.Key == key && x.Id != f.Id, ct))
            throw ValidationFailedException.For("key", $"Bu formada “{key}” açarlı sahə artıq var.");

        var options = NormalizeOptions(req.FieldType, req.OptionsJson);
        if (req.IsSubmissionTitle && req.FieldType != CompetitionFieldType.ShortText)
            throw ValidationFailedException.For("isSubmissionTitle", "Təklifin adı yalnız qısa mətn sahəsi ola bilər.");

        if (req.IsSubmissionTitle)
        {
            var others = await db.CompetitionFields
                .Where(x => x.CompetitionId == f.CompetitionId && x.Id != f.Id && x.IsSubmissionTitle)
                .ToListAsync(ct);
            foreach (var o in others) o.IsSubmissionTitle = false;
        }

        f.Key = key;
        f.Label = req.Label.Trim();
        f.FieldType = req.FieldType;
        f.Placeholder = AdminSlugs.NullIfBlank(req.Placeholder);
        f.HelpText = AdminSlugs.NullIfBlank(req.HelpText);
        f.IsRequired = req.IsRequired;
        f.IsSubmissionTitle = req.IsSubmissionTitle;
        f.IsActive = req.IsActive;
        f.IsPublicAfterResults = req.IsPublicAfterResults;
        f.OptionsJson = options;
    }

    /// <summary>Validates OptionsJson for the type; File limits must stay inside the global upload policy.</summary>
    private string? NormalizeOptions(CompetitionFieldType type, string? json)
    {
        JsonElement? root = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { root = JsonDocument.Parse(json).RootElement.Clone(); }
            catch (JsonException) { throw ValidationFailedException.For("optionsJson", "Parametrlər düzgün JSON deyil."); }
        }

        switch (type)
        {
            case CompetitionFieldType.Select:
                if (root is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() == 0
                    || arr.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString())))
                    throw ValidationFailedException.For("optionsJson", "Seçim sahəsi üçün ən azı bir variant yazın.");
                break;

            case CompetitionFieldType.File:
            {
                var policy = uploads.Value;
                var global = policy.AllowedFileTypes;
                var errors = new List<string>();
                if (root is { ValueKind: JsonValueKind.Object } o)
                {
                    if (o.TryGetProperty("maxFileSizeMb", out var size)
                        && (!size.TryGetInt32(out var mb) || mb < 1 || mb > policy.MaxFileSizeMb))
                        errors.Add($"Fayl ölçüsü 1 ilə {policy.MaxFileSizeMb} MB arasında olmalıdır (serverin limiti).");
                    if (o.TryGetProperty("maxFiles", out var files)
                        && (!files.TryGetInt32(out var n) || n < 1 || n > policy.MaxFilesPerSubmission))
                        errors.Add($"Fayl sayı 1 ilə {policy.MaxFilesPerSubmission} arasında olmalıdır (serverin limiti).");
                    if (o.TryGetProperty("accept", out var accept))
                    {
                        var keys = accept.ValueKind == JsonValueKind.Array
                            ? accept.EnumerateArray().Select(a => a.GetString() ?? "").ToList()
                            : [];
                        if (keys.Count == 0)
                            errors.Add("Ən azı bir fayl formatı seçin.");
                        else if (keys.Any(k => global.All(t => !string.Equals(t.Key, k, StringComparison.OrdinalIgnoreCase))))
                            errors.Add($"Yalnız serverin icazə verdiyi formatlar seçilə bilər: {string.Join(", ", global.Select(t => t.Label))}.");
                    }
                }
                else if (root is not null)
                {
                    errors.Add("Fayl parametrləri obyekt olmalıdır.");
                }
                if (errors.Count > 0)
                    throw new ValidationFailedException(new Dictionary<string, string[]> { ["optionsJson"] = errors.ToArray() });
                break;
            }

            case CompetitionFieldType.ShortText or CompetitionFieldType.LongText
                when root is { ValueKind: JsonValueKind.Object } t && t.TryGetProperty("maxLength", out var max):
                if (!max.TryGetInt32(out var len) || len < 1)
                    throw ValidationFailedException.For("optionsJson", "Maksimal uzunluq müsbət rəqəm olmalıdır.");
                break;
        }
        return json is null ? null : AdminSlugs.NullIfBlank(json);
    }

    private async Task<bool> HasDataAsync(int fieldId, CancellationToken ct) =>
        await db.SubmissionAnswers.AnyAsync(a => a.CompetitionFieldId == fieldId && a.Value != null, ct)
        || await db.SubmissionFiles.AnyAsync(f => f.CompetitionFieldId == fieldId, ct);

    private async Task<CompetitionField> FindAsync(int competitionId, int fieldId, CancellationToken ct) =>
        await db.CompetitionFields.FirstOrDefaultAsync(f => f.Id == fieldId && f.CompetitionId == competitionId, ct)
        ?? throw new NotFoundException("Sahə tapılmadı.");

    private async Task EnsureCompetitionAsync(int id, CancellationToken ct)
    {
        if (!await db.Competitions.AnyAsync(c => c.Id == id, ct))
            throw new NotFoundException("Müsabiqə tapılmadı.");
    }
}
