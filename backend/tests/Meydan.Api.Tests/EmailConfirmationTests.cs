using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Meydan.Api.Tests;

/// <summary>
/// E-mail confirmation before voting: off unless configured and unless mail is actually sent; once on, only accounts
/// opened from the configured moment are asked; nobody is locked out of signing in; the link works once and the
/// session survives it; a new link is rationed.
/// </summary>
public sealed class EmailConfirmationTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";
    private const string Kind = "email-confirmation";

    private MeydanFactory _factory = null!;
    private DateTimeOffset _t0;

    public void Dispose() => _factory?.Dispose();

    private void Start(bool required = true, string mode = "Log")
    {
        var now = DateTimeOffset.UtcNow;
        _t0 = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        _factory = new MeydanFactory
        {
            Settings =
            {
                ["Email:OutboxWorkerEnabled"] = "false",
                ["Email:Mode"] = mode,
                ["Voting:LeaderboardCacheSeconds"] = "0",
            },
        };
        if (required) _factory.Settings["Auth:EmailConfirmation:RequiredForAccountsFrom"] = _t0.AddHours(-1).ToString("O");
        _factory.Clock.Now = _t0;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0
            ? default
            : await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed record Account(HttpClient Client, Guid Id, string Email);

    private async Task<Account> AccountAsync(string first = "Aysel", string last = "Məmmədova")
    {
        var client = _factory.CreateClient();
        var email = $"p-{Guid.NewGuid():N}@example.com";
        client.UseToken((await Json(await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = first, lastName = last, phone = TestPhones.Next(), email,
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        }))).GetProperty("accessToken").GetString()!);
        var id = (await Json(await client.GetAsync("/api/me/profile"))).GetProperty("id").GetGuid();
        return new Account(client, id, email);
    }

    private async Task<string> SubmitAndOpenAsync()
    {
        var author = await AccountAsync("Leyla", "Quliyeva");
        if (_factory.WithDb(db => db.Users.Single(u => u.Id == author.Id).EmailConfirmed) is false)
            await _factory.WithDbAsync(async db =>
            {
                (await db.Users.SingleAsync(u => u.Id == author.Id)).EmailConfirmed = true;
                await db.SaveChangesAsync();
            });
        var client = author.Client;
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

    private static Task<HttpResponseMessage> VoteAsync(HttpClient client, string reference) =>
        client.PostAsync($"/api/competitions/{Chevron}/voting/works/{reference}/vote", null);

    private int Rows(Guid userId) => _factory.WithDb(db => db.EmailOutbox.Count(m => m.UserId == userId && m.Kind == Kind));

    /// <summary>The link exactly as the mail would carry it, composed the way the outbox worker composes it.</summary>
    private async Task<(string Email, string Token)?> LinkAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Meydan.Infrastructure.Persistence.MeydanDbContext>();
        var row = await db.EmailOutbox.AsNoTracking().Where(m => m.UserId == userId && m.Kind == Kind).OrderByDescending(m => m.Id).FirstAsync();
        var mail = await scope.ServiceProvider.GetRequiredService<IEmailComposer>().ComposeAsync(row, CancellationToken.None);
        if (mail is null) return null;
        var m = Regex.Match(mail.TextBody, @"/confirm-email#email=([^&\s]+)&token=(\S+)");
        Assert.True(m.Success, mail.TextBody);
        return (Uri.UnescapeDataString(m.Groups[1].Value), m.Groups[2].Value);
    }

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string email, string token) =>
        client.PostAsJsonAsync("/api/auth/confirm-email", new { email, token });

    [Fact]
    public async Task Off_by_default_and_off_while_mail_is_not_sent()
    {
        foreach (var (required, mode) in new[] { (false, "Log"), (true, "Disabled"), (false, "Log-empty") })
        {
            Start(required, mode == "Log-empty" ? "Log" : mode);
            // As the container passes an unset value: an empty string, which must mean "off", not a startup error.
            if (mode == "Log-empty") _factory.Settings["Auth:EmailConfirmation:RequiredForAccountsFrom"] = "";
            var reference = await SubmitAndOpenAsync();
            var voter = await AccountAsync();
            Assert.Equal(0, Rows(voter.Id));
            await Json(await VoteAsync(voter.Client, reference));
            _factory.Dispose();
        }
        _factory = null!;
    }

    [Fact]
    public async Task A_new_account_is_sent_a_link_and_may_sign_in_but_not_vote_until_it_confirms()
    {
        Start();
        var reference = await SubmitAndOpenAsync();
        var voter = await AccountAsync();
        Assert.Equal(1, Rows(voter.Id));

        var ballot = await Json(await voter.Client.GetAsync($"/api/competitions/{Chevron}/voting"));
        Assert.Equal("EmailNotConfirmed", ballot.GetProperty("viewer").GetProperty("reason").GetString());
        Assert.False(ballot.GetProperty("viewer").GetProperty("mayVote").GetBoolean());

        var refused = await VoteAsync(voter.Client, reference);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("e-mail ünvanınızı təsdiqləyin", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, _factory.WithDb(db => db.Votes.IgnoreQueryFilters().Count(v => v.UserId == voter.Id)));

        // Signing in again works as always.
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = voter.Email, password = "secret123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // The link: confirmed, the session still good, and the vote goes through.
        var (email, token) = (await LinkAsync(voter.Id))!.Value;
        Assert.Equal(voter.Email, email);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(_factory.CreateClient(), email, token)).StatusCode);
        Assert.True((await Json(await voter.Client.GetAsync("/api/me/profile"))).GetProperty("emailConfirmed").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(_factory.CreateClient(), email, token)).StatusCode);   // twice is fine
        await Json(await VoteAsync(voter.Client, reference));
        Assert.Null(await LinkAsync(voter.Id));   // a link still queued is not sent once there is nothing to confirm
    }

    [Fact]
    public async Task Accounts_opened_before_the_requirement_are_never_asked()
    {
        Start();
        var reference = await SubmitAndOpenAsync();
        var older = await AccountAsync();
        await _factory.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == older.Id)).CreatedAt = _t0.AddDays(-3);
            await db.SaveChangesAsync();
        });
        var ballot = await Json(await older.Client.GetAsync($"/api/competitions/{Chevron}/voting"));
        Assert.Equal("Allowed", ballot.GetProperty("viewer").GetProperty("reason").GetString());
        await Json(await VoteAsync(older.Client, reference));
    }

    [Fact]
    public async Task A_wrong_or_borrowed_link_confirms_nothing()
    {
        Start();
        var a = await AccountAsync();
        var b = await AccountAsync("Orxan", "Məmmədov");
        var (_, tokenA) = (await LinkAsync(a.Id))!.Value;

        foreach (var (email, token) in new[] { (a.Email, "not-a-token"), (a.Email, ""), (b.Email, tokenA), ("nobody@example.com", tokenA) })
        {
            var res = await ConfirmAsync(_factory.CreateClient(), email, token);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
        Assert.False(_factory.WithDb(db => db.Users.Single(u => u.Id == a.Id).EmailConfirmed));
        Assert.False(_factory.WithDb(db => db.Users.Single(u => u.Id == b.Id).EmailConfirmed));

        // A changed password (new security stamp) ends an outstanding link too.
        await _factory.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == a.Id)).SecurityStamp = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmAsync(_factory.CreateClient(), a.Email, tokenA)).StatusCode);
    }

    [Fact]
    public async Task A_new_link_once_a_minute_five_a_day_and_none_once_confirmed()
    {
        Start();
        var a = await AccountAsync();
        const string url = "/api/auth/email-confirmation";

        var tooSoon = await a.Client.PostAsync(url, null);   // registration sent one a moment ago
        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);
        Assert.Contains("Bir dəqiqə sonra", await tooSoon.Content.ReadAsStringAsync());

        for (var i = 1; i < 5; i++)
        {
            _factory.Clock.Now = _factory.Clock.Now.AddMinutes(2);
            Assert.Equal(HttpStatusCode.Accepted, (await a.Client.PostAsync(url, null)).StatusCode);
        }
        Assert.Equal(5, Rows(a.Id));
        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(2);
        var daily = await a.Client.PostAsync(url, null);
        Assert.Equal(HttpStatusCode.TooManyRequests, daily.StatusCode);
        Assert.Contains("Sabah", await daily.Content.ReadAsStringAsync());

        // A day later it may ask again; once confirmed there is nothing to send.
        _factory.Clock.Now = _factory.Clock.Now.AddDays(1);
        Assert.Equal(HttpStatusCode.Accepted, (await a.Client.PostAsync(url, null)).StatusCode);
        var (email, token) = (await LinkAsync(a.Id))!.Value;
        await Json(await ConfirmAsync(_factory.CreateClient(), email, token));
        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(2);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.PostAsync(url, null)).StatusCode);
        Assert.Equal(6, Rows(a.Id));

        // Signed out: no link for anyone.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsync(url, null)).StatusCode);
    }
}
