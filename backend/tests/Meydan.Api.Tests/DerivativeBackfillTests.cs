using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// Phase 5 final review: public derivatives of earlier uploads are rebuilt in the background in small, paced batches.
/// Startup is not held up, new uploads go first, and a missing or malformed original ends as Failed (placeholder) —
/// it never crashes the worker, never stays Pending forever, and the original is never served instead.
/// </summary>
public sealed class DerivativeBackfillTests
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";

    private static async Task<List<int>> SubmitAsync(HttpClient client, string settlement, int files)
    {
        client.UseToken(await client.RegisterAsync($"b-{Guid.NewGuid():N}@example.com"));
        var draft = await (await client.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = Chevron, categorySlug = settlement })).Content.ReadFromJsonAsync<JsonElement>();
        var id = draft.GetProperty("id").GetInt32();
        (await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title = "İş", concept = "K", symbols = "S" } }))
            .EnsureSuccessStatusCode();
        var ids = new List<int>();
        for (var i = 0; i < files; i++)
        {
            var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
            var file = new ByteArrayContent(TestImages.Png(200 + i, 150));
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", $"f{i}.png");
            var res = await client.PostAsync($"/api/submissions/{id}/files", form);
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            ids.Add((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32());
        }
        (await client.PostAsync($"/api/submissions/{id}/submit", null)).EnsureSuccessStatusCode();
        return ids;
    }

    private static Dictionary<int, (PublicAssetStatus Status, string? Error)> States(MeydanFactory factory) =>
        factory.WithDb(db => db.SubmissionFiles.AsNoTracking()
            .ToDictionary(f => f.Id, f => (f.PublicAssetStatus, f.PublicAssetError)));

    [Fact]
    public async Task A_backlog_is_worked_off_in_paced_batches_behind_new_uploads_and_bad_files_end_as_failed()
    {
        using var factory = new MeydanFactory();
        var backlog = new List<int>();

        // Process 1: ten files are uploaded and converted normally.
        using (var first = factory.WithWebHostBuilder(_ => { }))
        {
            backlog.AddRange(await SubmitAsync(first.CreateClient(), "mastaga", 5));
            backlog.AddRange(await SubmitAsync(first.CreateClient(), "nardaran", 5));
            for (var i = 0; i < 200 && States(factory).Values.Any(s => s.Status == PublicAssetStatus.Pending); i++)
                await Task.Delay(50);
            Assert.All(States(factory).Values, s => Assert.Equal(PublicAssetStatus.Ready, s.Status));
        }

        // Between runs every derivative becomes pending again (think: restored database), one original disappears
        // and another is damaged on disk.
        await factory.WithDbAsync(db => db.SubmissionFiles.ExecuteUpdateAsync(s => s
            .SetProperty(f => f.PublicAssetStatus, PublicAssetStatus.Pending)
            .SetProperty(f => f.PublicAssetKey, (string?)null)));
        var keys = factory.WithDb(db => db.SubmissionFiles.ToDictionary(f => f.Id, f => f.StoragePath));
        File.Delete(Path.Combine(factory.StorageRoot, keys[backlog[0]]));
        var damaged = TestImages.Png(200, 150);
        Array.Fill(damaged, (byte)0x5A, 40, damaged.Length - 40); // valid signature/header, garbage pixels
        await File.WriteAllBytesAsync(Path.Combine(factory.StorageRoot, keys[backlog[1]]), damaged);

        // Process 2: slow, small backfill batches.
        var watch = Stopwatch.StartNew();
        using var second = factory.WithWebHostBuilder(b => b
            .UseSetting("Uploads:DerivativeBackfillBatchSize", "3")
            // Slow enough that the backlog visibly outlasts a fresh upload even when the whole suite runs in parallel.
            .UseSetting("Uploads:DerivativeBackfillPauseMs", "1500"));
        var client = second.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"startup took {watch.Elapsed}");
        Assert.True(States(factory).Values.Count(s => s.Status == PublicAssetStatus.Pending) >= 5,
            "the backlog is paced, not converted all at once during startup");

        // A new upload is converted ahead of the remaining backlog.
        var fresh = (await SubmitAsync(second.CreateClient(), "zire", 1)).Single();
        var states = States(factory);
        for (var i = 0; i < 100 && states[fresh].Status == PublicAssetStatus.Pending; i++)
        {
            await Task.Delay(50);
            states = States(factory);
        }
        Assert.Equal(PublicAssetStatus.Ready, states[fresh].Status);
        Assert.Contains(backlog, id => states[id].Status == PublicAssetStatus.Pending);

        // Eventually everything is settled: nothing left pending, bad files failed with a reason, the rest ready.
        for (var i = 0; i < 900 && states.Values.Any(s => s.Status == PublicAssetStatus.Pending); i++)
        {
            await Task.Delay(50);
            states = States(factory);
        }
        Assert.Equal((PublicAssetStatus.Failed, "original-missing"), states[backlog[0]]);
        Assert.Equal(PublicAssetStatus.Failed, states[backlog[1]].Status);
        Assert.All(backlog.Skip(2), id => Assert.Equal(PublicAssetStatus.Ready, states[id].Status));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("Unhandled") || l.Contains("postponed"));

        // The worker is still alive afterwards.
        var later = (await SubmitAsync(second.CreateClient(), "mastaga", 1)).Single();
        for (var i = 0; i < 100 && States(factory)[later].Status == PublicAssetStatus.Pending; i++)
            await Task.Delay(50);
        Assert.Equal(PublicAssetStatus.Ready, States(factory)[later].Status);
    }
}
