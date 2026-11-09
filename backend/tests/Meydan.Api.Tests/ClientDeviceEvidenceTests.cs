using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// D20: every sign-in and every vote keeps the browser's User-Agent and the frontend's random device id, as evidence
/// for a later audit. Nothing is refused because of them, a malformed id is dropped and an overlong User-Agent is cut.
/// </summary>
public sealed class ClientDeviceEvidenceTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";

    private readonly MeydanFactory _factory;
    private readonly DateTimeOffset _t0;

    public ClientDeviceEvidenceTests()
    {
        var now = DateTimeOffset.UtcNow;
        _t0 = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        _factory = new MeydanFactory
        {
            Settings =
            {
                ["Email:OutboxWorkerEnabled"] = "false",
                ["Voting:LeaderboardCacheSeconds"] = "0",
            },
        };
        _factory.Clock.Now = _t0;
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0
            ? default
            : await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(HttpClient Client, Guid Id)> AccountAsync(string first, string last, string? userAgent, string? deviceId)
    {
        var client = _factory.CreateClient();
        if (userAgent is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        if (deviceId is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("X-Device-Id", deviceId);
        client.UseToken((await Json(await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = first, lastName = last, phone = TestPhones.Next(), email = $"d-{Guid.NewGuid():N}@example.com",
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        }))).GetProperty("accessToken").GetString()!);
        var id = (await Json(await client.GetAsync("/api/me/profile"))).GetProperty("id").GetGuid();
        return (client, id);
    }

    private async Task<string> SubmitAndOpenAsync()
    {
        var (client, _) = await AccountAsync("Leyla", "Quliyeva", null, null);
        var id = (await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "balaxani" })))
            .GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title = "Balaxanı şevronu", concept = "K", symbols = "S" } }));
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "eskiz.png");
        await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        var reference = (await Json(await client.PostAsync($"/api/submissions/{id}/submit", null))).GetProperty("referenceNumber").GetString()!;
        await _factory.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync(x => x.Slug == Chevron);
            c.VotingStartsAt = _t0.AddSeconds(5);
            c.VotingEndsAt = _t0.AddHours(2);
            await db.SaveChangesAsync();
        });
        _factory.Clock.Now = _t0.AddSeconds(10);
        return reference;
    }

    [Fact]
    public async Task A_sign_in_and_a_vote_keep_the_browser_and_its_device_id()
    {
        var reference = await SubmitAndOpenAsync();
        const string phone = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) Mobile/15E148";
        var (voter, voterId) = await AccountAsync("Aysel", "Məmmədova", phone, "3f2a9c1e-5b7d-4e8a-9c0f-1a2b3c4d5e6f");

        await Json(await voter.PostAsync($"/api/competitions/{Chevron}/voting/works/{reference}/vote", null));

        var token = _factory.WithDb(db => db.RefreshTokens.AsNoTracking().Where(t => t.UserId == voterId).OrderBy(t => t.Id).First());
        Assert.Equal(phone, token.CreatedByUserAgent);
        Assert.Equal("3f2a9c1e-5b7d-4e8a-9c0f-1a2b3c4d5e6f", token.CreatedByDeviceId);

        var vote = _factory.WithDb(db => db.Votes.AsNoTracking().Single(v => v.UserId == voterId));
        Assert.Equal(phone, vote.CastUserAgent);
        Assert.Equal("3f2a9c1e-5b7d-4e8a-9c0f-1a2b3c4d5e6f", vote.CastDeviceId);
    }

    [Fact]
    public async Task A_malformed_device_id_is_dropped_and_an_overlong_browser_string_is_cut_but_the_vote_counts()
    {
        var reference = await SubmitAndOpenAsync();
        var longAgent = "Agent/" + new string('x', 900);
        var (voter, voterId) = await AccountAsync("Nərmin", "Əliyeva", longAgent, "not a device id!");

        var receipt = await Json(await voter.PostAsync($"/api/competitions/{Chevron}/voting/works/{reference}/vote", null));
        Assert.Equal(JsonValueKind.Object, receipt.ValueKind);

        var vote = _factory.WithDb(db => db.Votes.AsNoTracking().Single(v => v.UserId == voterId));
        Assert.Null(vote.CastDeviceId);
        Assert.Equal(512, vote.CastUserAgent!.Length);
        Assert.StartsWith("Agent/xxx", vote.CastUserAgent);

        var token = _factory.WithDb(db => db.RefreshTokens.AsNoTracking().First(t => t.UserId == voterId));
        Assert.Null(token.CreatedByDeviceId);
        Assert.Equal(512, token.CreatedByUserAgent!.Length);
    }

    [Fact]
    public async Task A_request_without_either_still_signs_in_and_votes()
    {
        var reference = await SubmitAndOpenAsync();
        var (voter, voterId) = await AccountAsync("Kamran", "Quliyev", null, null);

        await Json(await voter.PostAsync($"/api/competitions/{Chevron}/voting/works/{reference}/vote", null));

        var vote = _factory.WithDb(db => db.Votes.AsNoTracking().Single(v => v.UserId == voterId));
        Assert.Null(vote.CastDeviceId);
    }
}
