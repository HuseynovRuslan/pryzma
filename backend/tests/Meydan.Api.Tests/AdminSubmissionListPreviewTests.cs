using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// The admin list carries, for every work on the page, the file it is drawn with and whether that file's small copy
/// exists — so the table and the gallery draw a whole page of pictures from one list response, never one request per
/// work and never an original.
/// </summary>
public sealed partial class AdminSubmissionListPreviewTests : IDisposable
{
    // SQL is logged for this class only, so the test can see every command the list request runs.
    private readonly MeydanFactory _factory = new()
    {
        Settings = { ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Information" },
    };

    public void Dispose() => _factory.Dispose();

    private static SubmissionFile File(string name, string type, int sort, bool thumbReady) => new()
    {
        OriginalFileName = name,
        StoragePath = $"2026/09/{Guid.NewGuid():N}{Path.GetExtension(name)}",
        ContentType = type,
        SizeBytes = 1000,
        SortOrder = sort,
        UploadedAt = DateTimeOffset.UtcNow,
        PublicAssetStatus = thumbReady ? PublicAssetStatus.Ready : PublicAssetStatus.Pending,
        ThumbnailStatus = thumbReady ? PublicAssetStatus.Ready : PublicAssetStatus.Pending,
        ThumbnailKey = thumbReady ? $"derived/2026/09/{Guid.NewGuid():N}.webp" : null,
    };

    [Fact]
    public async Task Every_row_names_its_cover_and_its_small_copy_in_the_one_list_query()
    {
        var owner = _factory.CreateClient();
        await owner.RegisterAsync($"p-{Guid.NewGuid():N}@example.com");
        var ids = new Dictionary<string, int>();
        var files = new Dictionary<string, int>();

        await _factory.WithDbAsync(async db =>
        {
            var user = await db.Users.OrderByDescending(u => u.CreatedAt).FirstAsync();
            var competition = await db.Competitions.FirstAsync(c => c.Slug == DbSeeder.ChevronCompetitionSlug);
            Submission Work(string reference, params SubmissionFile[] attached)
            {
                var s = new Submission
                {
                    UserId = user.Id, CompetitionId = competition.Id, Title = reference, Status = SubmissionStatus.Submitted,
                    ReferenceNumber = reference, SubmittedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                foreach (var f in attached) s.Files.Add(f);
                db.Submissions.Add(s);
                return s;
            }

            // A PDF first and a picture second: the list draws the picture.
            var mixed = Work("SUB-T-MIXED", File("izah.pdf", "application/pdf", 0, true), File("eskiz.png", "image/png", 1, true));
            // Only a PDF: its copy is its first page.
            var pdf = Work("SUB-T-PDF", File("teqdimat.pdf", "application/pdf", 0, true));
            // A picture whose small copy is still being made.
            var pending = Work("SUB-T-PENDING", File("foto.jpg", "image/jpeg", 0, false));
            // No file at all.
            var empty = Work("SUB-T-EMPTY");
            // The admin chose the second picture for the gallery: the list follows the choice.
            var chosen = Work("SUB-T-CHOSEN", File("bir.png", "image/png", 0, true), File("iki.png", "image/png", 1, true));
            await db.SaveChangesAsync();

            chosen.PreviewFileId = chosen.Files.Single(f => f.OriginalFileName == "iki.png").Id;
            await db.SaveChangesAsync();

            foreach (var s in new[] { mixed, pdf, pending, empty, chosen })
                ids[s.ReferenceNumber!] = s.Id;
            files["mixed-png"] = mixed.Files.Single(f => f.OriginalFileName == "eskiz.png").Id;
            files["pdf"] = pdf.Files.Single().Id;
            files["pending"] = pending.Files.Single().Id;
            files["chosen"] = chosen.PreviewFileId!.Value;
        });

        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));

        var before = _factory.Logs.Lines.Count;
        var res = await admin.GetAsync("/api/admin/submissions?q=SUB-T-&pageSize=50");
        var commands = _factory.Logs.Lines.Skip(before).Where(l => l.Contains("Executed DbCommand", StringComparison.Ordinal)).ToList();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var list = await res.Content.ReadFromJsonAsync<JsonElement>();
        var rows = list.GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("referenceNumber").GetString()!);
        Assert.Equal(5, rows.Count);

        JsonElement Preview(string reference) => rows[reference].GetProperty("preview");

        Assert.Equal(files["mixed-png"], Preview("SUB-T-MIXED").GetProperty("fileId").GetInt32());
        Assert.True(Preview("SUB-T-MIXED").GetProperty("thumbnailReady").GetBoolean());
        Assert.Equal(2, rows["SUB-T-MIXED"].GetProperty("fileCount").GetInt32());

        Assert.Equal(files["pdf"], Preview("SUB-T-PDF").GetProperty("fileId").GetInt32());
        Assert.True(Preview("SUB-T-PDF").GetProperty("thumbnailReady").GetBoolean());

        Assert.Equal(files["pending"], Preview("SUB-T-PENDING").GetProperty("fileId").GetInt32());
        Assert.False(Preview("SUB-T-PENDING").GetProperty("thumbnailReady").GetBoolean());

        Assert.Equal(JsonValueKind.Null, Preview("SUB-T-EMPTY").ValueKind);
        Assert.Equal(0, rows["SUB-T-EMPTY"].GetProperty("fileCount").GetInt32());

        Assert.Equal(files["chosen"], Preview("SUB-T-CHOSEN").GetProperty("fileId").GetInt32());

        // No N+1: the files are read inside the page query itself. A query per work would filter the files by one
        // submission's id; background workers that poll the files table in the meantime never do.
        Assert.NotEmpty(commands);
        Assert.DoesNotContain(commands, c => c.Contains("\"SubmissionFiles\"", StringComparison.Ordinal) && PerSubmissionFilter().IsMatch(c));
        Assert.Single(commands, c => c.Contains("FROM \"Submissions\"", StringComparison.Ordinal)
                                     && c.Contains("\"SubmissionFiles\"", StringComparison.Ordinal)
                                     && c.Contains("LIMIT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_list_and_its_small_copies_stay_admin_only()
    {
        var participant = _factory.CreateClient();
        participant.UseToken(await participant.RegisterAsync($"p-{Guid.NewGuid():N}@example.com"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/admin/submissions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.GetAsync("/api/admin/submissions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.GetAsync("/api/admin/submissions/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.GetAsync("/api/admin/submissions/1/files/1/public")).StatusCode);
    }

    [GeneratedRegex("\"SubmissionId\"\\s*=\\s*@|@\\w+\\s*=\\s*\\w+\\.\"SubmissionId\"")]
    private static partial Regex PerSubmissionFilter();
}
