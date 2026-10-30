using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meydan.Api.Features.Auth;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Infrastructure.Email;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meydan.Api.Tests;

/// <summary>
/// Email-first registration: no account until the address is proven; the link is one-time and short-lived; the proof
/// makes one account at most, even under a race; an address that already has an account learns nothing from the form;
/// resends are rationed; the phone rule and the robot check still hold; everyone else is untouched.
/// </summary>
public sealed class EmailFirstRegistrationTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";

    private sealed class FakeCaptcha : ICaptchaVerifier
    {
        public CaptchaOutcome Outcome { get; set; } = CaptchaOutcome.Passed;
        public Task<CaptchaOutcome> VerifyAsync(string? token, string action, string? ip, CancellationToken ct) =>
            Task.FromResult(token is null ? CaptchaOutcome.Failed : Outcome);
    }

    /// <summary>Test-only: the client address comes from an "X-Test-Ip" header, so two callers can be told apart.</summary>
    private sealed class TestClientIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext http, RequestDelegate nextMw) =>
            {
                if (http.Request.Headers.TryGetValue("X-Test-Ip", out var ip))
                    http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip.ToString());
                return nextMw(http);
            });
            next(app);
        };
    }

    private readonly FakeCaptcha _captcha = new();
    private MeydanFactory _f = null!;
    private DateTimeOffset _t0;

    public void Dispose() => _f?.Dispose();

    private void Start(bool emailFirst = true, bool captchaOn = false, int? startsPer10Minutes = null)
    {
        var now = DateTimeOffset.UtcNow;
        _t0 = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        _f = new MeydanFactory
        {
            Settings =
            {
                ["Email:OutboxWorkerEnabled"] = "false",
                ["Email:Mode"] = "Log",
                ["Voting:LeaderboardCacheSeconds"] = "0",
                ["Auth:Registration:EmailFirst"] = emailFirst ? "true" : "false",
                // E-mail confirmation before voting is on too, as in production: a completed account must pass it.
                ["Auth:EmailConfirmation:RequiredForAccountsFrom"] = _t0.AddHours(-1).ToString("O"),
            },
            Overrides = s =>
            {
                s.RemoveAll<ICaptchaVerifier>();
                s.AddSingleton<ICaptchaVerifier>(_captcha);
                s.AddSingleton<IStartupFilter, TestClientIp>();
            },
        };
        if (startsPer10Minutes is { } limit) _f.Settings["RateLimiting:RegistrationStartsPer10Minutes"] = limit.ToString();
        if (captchaOn)
        {
            _f.Settings["Captcha:Provider"] = "Turnstile";
            _f.Settings["Captcha:SiteKey"] = "site";
            _f.Settings["Captcha:SecretKey"] = "secret";
        }
        _f.Clock.Now = _t0;
    }

    private static string NewEmail() => $"r-{Guid.NewGuid():N}@example.com";

    private Task<HttpResponseMessage> StartAsync(HttpClient c, string email, string? captchaToken = "ok") =>
        c.PostAsJsonAsync("/api/auth/registration/start", new { email, captchaToken });

    /// <summary>The link exactly as the newest mail for this address would carry it, composed as the worker composes it.</summary>
    private async Task<string?> LinkAsync(string email)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Meydan.Infrastructure.Persistence.MeydanDbContext>();
        var normalized = email.ToUpperInvariant();
        var pending = await db.PendingRegistrations.AsNoTracking().SingleOrDefaultAsync(p => p.NormalizedEmail == normalized);
        if (pending is null) return null;
        var row = await db.EmailOutbox.AsNoTracking().Where(m => m.PendingRegistrationId == pending.Id).OrderByDescending(m => m.Id).FirstAsync();
        var mail = await scope.ServiceProvider.GetRequiredService<IEmailComposer>().ComposeAsync(row, CancellationToken.None);
        if (mail is null) return null;
        Assert.Equal(email, mail.To);
        Assert.EndsWith(": e-mailinizi təsdiqləyin", mail.Subject);
        Assert.Contains("Qeydiyyatı davam etdirmək üçün e-mail ünvanınızı təsdiqləyin.", mail.TextBody);
        var m = Regex.Match(mail.TextBody, @"/register/verify#token=(\S+)");
        Assert.True(m.Success, mail.TextBody);
        return m.Groups[1].Value;
    }

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient c, string token) =>
        c.PostAsJsonAsync("/api/auth/registration/verify", new { token });

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient c, string proof, string? phone = null, string first = "Aysel") =>
        c.PostAsJsonAsync("/api/auth/registration/complete", new
        {
            proof, firstName = first, lastName = "Məmmədova", phone = phone ?? TestPhones.Next(),
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });

    private async Task<string> ProofAsync(HttpClient c, string email)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(c, email)).StatusCode);
        var token = (await LinkAsync(email))!;
        var res = await VerifyAsync(c, token);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(email, body.GetProperty("email").GetString());
        return body.GetProperty("proof").GetString()!;
    }

    /// <summary>A whole registration through the three steps; the client ends signed in.</summary>
    private async Task<(HttpClient Client, string Email)> RegisterAsync(string first = "Aysel")
    {
        var c = _f.CreateClient();
        var email = NewEmail();
        var res = await CompleteAsync(c, await ProofAsync(c, email), first: first);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        c.UseToken((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);
        return (c, email);
    }

    private int Users(string email) => _f.WithDb(db => db.Users.Count(u => u.NormalizedEmail == email.ToUpperInvariant()));
    private int Pending() => _f.WithDb(db => db.PendingRegistrations.Count());

    // ---- 1, 3: the first step creates no account and queues a link --------------------------------

    [Fact]
    public async Task An_address_alone_creates_no_account_keeps_nothing_else_and_queues_a_link()
    {
        Start();
        var c = _f.CreateClient();
        var usersBefore = _f.WithDb(db => db.Users.Count());
        var email = NewEmail();
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(c, email)).StatusCode);

        Assert.Equal(usersBefore, _f.WithDb(db => db.Users.Count()));
        var p = _f.WithDb(db => db.PendingRegistrations.Single());
        Assert.Equal(email, p.Email);
        Assert.Equal(0, _f.WithDb(db => db.PendingRegistrationLinks.Count()));   // no token exists until the mail is composed
        Assert.Null(p.ProofHash);
        Assert.Equal(1, _f.WithDb(db => db.EmailOutbox.Count(m => m.PendingRegistrationId == p.Id && m.UserId == null && m.Kind == "registration-verify")));

        var token = await LinkAsync(email);
        var link = _f.WithDb(db => db.PendingRegistrationLinks.Single());
        Assert.Equal(p.Id, link.PendingRegistrationId);
        Assert.DoesNotContain(token!, link.TokenHash);                          // only the hash is kept
        Assert.Equal(_t0.AddMinutes(45), link.ExpiresAt);
    }

    // ---- 2, 16: the robot check -------------------------------------------------------------------

    [Fact]
    public async Task A_failed_robot_check_creates_nothing()
    {
        Start(captchaOn: true);
        var c = _f.CreateClient();
        _captcha.Outcome = CaptchaOutcome.Failed;
        var res = await StartAsync(c, NewEmail());
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("\"captcha\"", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(c, NewEmail(), captchaToken: null)).StatusCode);
        Assert.Equal(0, Pending());
        Assert.Equal(0, _f.WithDb(db => db.EmailOutbox.Count()));

        _captcha.Outcome = CaptchaOutcome.Passed;
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(c, NewEmail())).StatusCode);
    }

    // ---- 4, 5, 6: the link ------------------------------------------------------------------------

    [Fact]
    public async Task A_wrong_link_fails_and_a_link_works_once()
    {
        Start();
        var c = _f.CreateClient();
        var email = NewEmail();
        await StartAsync(c, email);
        var token = (await LinkAsync(email))!;

        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyAsync(c, "not-a-token")).StatusCode);
        Assert.True((await VerifyAsync(c, token)).IsSuccessStatusCode);
        var replay = await VerifyAsync(c, token);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("istifadə olunub", await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_old_link_fails()
    {
        Start();
        var c = _f.CreateClient();
        var email = NewEmail();
        await StartAsync(c, email);
        var token = (await LinkAsync(email))!;
        _f.Clock.Now = _t0.AddMinutes(46);
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyAsync(c, token)).StatusCode);
        Assert.Equal(0, Users(email));
    }

    // ---- 7, 8, 9, 17: completion -------------------------------------------------------------------

    [Fact]
    public async Task Without_a_proven_address_there_is_no_completion()
    {
        Start();
        var c = _f.CreateClient();
        var email = NewEmail();
        await StartAsync(c, email);
        await LinkAsync(email);                       // a link exists, but it has not been opened
        var res = await CompleteAsync(c, "made-up-proof");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("\"proof\"", await res.Content.ReadAsStringAsync());
        Assert.Equal(0, Users(email));
    }

    [Fact]
    public async Task A_proven_address_completes_into_a_confirmed_signed_in_account_that_votes_at_once()
    {
        Start();
        var (author, _) = await RegisterAsync("Leyla");
        var reference = await SubmitAsync(author);
        var (voter, email) = await RegisterAsync();

        var user = _f.WithDb(db => db.Users.Single(u => u.NormalizedEmail == email.ToUpperInvariant()));
        Assert.True(user.EmailConfirmed);
        Assert.Equal(0, Pending());
        var me = await (await voter.GetAsync("/api/me/profile")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(me.GetProperty("emailConfirmed").GetBoolean());

        var vote = await voter.PostAsync($"/api/competitions/{Chevron}/voting/works/{reference}/vote", null);
        Assert.True(vote.IsSuccessStatusCode, await vote.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_expired_proof_creates_nothing_and_sends_the_person_back_to_the_link()
    {
        Start();
        var c = _f.CreateClient();
        var email = NewEmail();
        var proof = await ProofAsync(c, email);
        _f.Clock.Now = _t0.AddMinutes(31);
        var res = await CompleteAsync(c, proof);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("yenidən təsdiqləyin", await res.Content.ReadAsStringAsync());
        Assert.Equal(0, Users(email));
    }

    // ---- 10, 11: one proof, one account ------------------------------------------------------------

    [Fact]
    public async Task A_proof_makes_one_account_one_after_the_other_and_at_the_same_moment()
    {
        Start();
        var c = _f.CreateClient();
        var email = NewEmail();
        var proof = await ProofAsync(c, email);
        Assert.True((await CompleteAsync(c, proof)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CompleteAsync(c, proof)).StatusCode);
        Assert.Equal(1, Users(email));

        var email2 = NewEmail();
        var proof2 = await ProofAsync(c, email2);
        var both = await Task.WhenAll(
            CompleteAsync(_f.CreateClient(), proof2), CompleteAsync(_f.CreateClient(), proof2),
            CompleteAsync(_f.CreateClient(), proof2));
        Assert.Equal(1, both.Count(r => r.IsSuccessStatusCode));
        Assert.Equal(1, Users(email2));
    }

    // ---- 12: an address that already has an account ------------------------------------------------

    [Fact]
    public async Task An_address_with_an_account_is_told_so_plainly_and_cannot_get_a_second_account()
    {
        Start();
        var (_, email) = await RegisterAsync();
        var usersBefore = _f.WithDb(db => db.Users.Count());
        var outboxBefore = _f.WithDb(db => db.EmailOutbox.Count());

        var res = await StartAsync(_f.CreateClient(), email.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("Bu e-mail ilə artıq hesab mövcuddur", body);
        Assert.DoesNotContain("Link göndərilmədi", body);
        Assert.Equal(0, Pending());                                   // nothing created
        Assert.Equal(usersBefore, _f.WithDb(db => db.Users.Count()));
        // The account's owner still hears about the attempt (rationed).
        Assert.Equal(1, _f.WithDb(db => db.EmailOutbox.Count(m => m.Kind == "registration-exists")));
        Assert.Equal(outboxBefore + 1, _f.WithDb(db => db.EmailOutbox.Count()));

        // An account made for the address while a registration was half-way: the completion refuses.
        var c = _f.CreateClient();
        var other = NewEmail();
        var proof = await ProofAsync(c, other);
        await _f.WithDbAsync(async db =>
        {
            var twin = new Meydan.Domain.Entities.ApplicationUser
            {
                UserName = other, Email = other, NormalizedEmail = other.ToUpperInvariant(), NormalizedUserName = other.ToUpperInvariant(),
                FirstName = "X", LastName = "Y", CreatedAt = _t0, UpdatedAt = _t0, SecurityStamp = Guid.NewGuid().ToString(),
            };
            db.Users.Add(twin);
            await db.SaveChangesAsync();
        });
        var dup = await CompleteAsync(c, proof);
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        Assert.Contains("artıq hesab var", await dup.Content.ReadAsStringAsync());
        Assert.Equal(1, Users(other));
    }

    // ---- the per-address limit on the first step ---------------------------------------------------

    private HttpClient From(string ip)
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Ip", ip);
        return c;
    }

    [Fact]
    public async Task Ten_starts_per_address_in_ten_minutes_then_429_and_another_address_is_not_affected()
    {
        Start(startsPer10Minutes: 10);
        var a = From("198.51.100.7");
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(a, NewEmail())).StatusCode);
        var refused = await StartAsync(a, NewEmail());
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("Çox sayda cəhd edildi. Bir qədər sonra yenidən yoxlayın.", await refused.Content.ReadAsStringAsync());
        Assert.Equal(10, Pending());                                   // the refused one left nothing behind

        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(From("203.0.113.9"), NewEmail())).StatusCode);

        // Only this endpoint: the same address still signs in.
        var admin = From("198.51.100.7");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/auth/login",
            new { email = MeydanFactory.AdminEmail, password = _f.AdminPassword })).StatusCode);
    }

    [Fact]
    public async Task Probing_existing_addresses_counts_against_the_same_limit_and_needs_the_robot_check()
    {
        Start(captchaOn: true, startsPer10Minutes: 3);
        var (_, email) = await RegisterAsync();
        var p = From("198.51.100.8");
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(p, email, captchaToken: null)).StatusCode);   // no robot check, no answer
        Assert.Equal(HttpStatusCode.Conflict, (await StartAsync(p, email)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await StartAsync(p, email)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await StartAsync(p, email)).StatusCode);
    }

    // ---- 13: resend limits --------------------------------------------------------------------------

    [Fact]
    public async Task A_new_link_once_a_minute_ten_a_day_and_every_link_sent_opens_until_it_runs_out()
    {
        Start();
        var c = _f.CreateClient();
        var email = NewEmail();
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(c, email)).StatusCode);
        var first = (await LinkAsync(email))!;
        var soon = await StartAsync(c, email);
        Assert.Equal(HttpStatusCode.TooManyRequests, soon.StatusCode);
        Assert.Contains("Bir dəqiqə sonra", await soon.Content.ReadAsStringAsync());

        var links = new List<string> { first };
        for (var i = 2; i <= 10; i++)
        {
            _f.Clock.Now = _f.Clock.Now.AddMinutes(2);
            Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(c, email)).StatusCode);
            links.Add((await LinkAsync(email))!);
        }
        _f.Clock.Now = _f.Clock.Now.AddMinutes(2);                                   // 20 minutes after the first mail
        var daily = await StartAsync(c, email);
        Assert.Equal(HttpStatusCode.TooManyRequests, daily.StatusCode);
        var limit = await daily.Content.ReadAsStringAsync();
        Assert.Contains("10 link", limit);
        Assert.Contains("45 dəqiqə", limit);
        Assert.Contains("sabah", limit);

        // A later mail did not cancel the first one (the owner, 21 October 2026); each link still works once.
        Assert.True((await VerifyAsync(c, first)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyAsync(c, first)).StatusCode);

        // Each runs out on its own clock: at 50 minutes the second mail's link is dead, the last one's is not.
        _f.Clock.Now = _t0.AddMinutes(50);
        var dead = await VerifyAsync(c, links[1]);
        Assert.Equal(HttpStatusCode.BadRequest, dead.StatusCode);
        Assert.Contains("Ən son gələn məktubdakı linki açın", await dead.Content.ReadAsStringAsync());
        Assert.True((await VerifyAsync(c, links[^1])).IsSuccessStatusCode);

        // The next day the count starts again.
        _f.Clock.Now = _t0.AddDays(1).AddMinutes(1);
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(c, email)).StatusCode);
    }

    [Fact]
    public async Task Asking_again_keeps_the_open_form_working_and_every_link_goes_with_the_account()
    {
        Start();
        var c = _f.CreateClient();
        var email = NewEmail();
        var proof = await ProofAsync(c, email);

        // The form is open, and the person asks for another mail anyway: it is sent, and the open form still works.
        _f.Clock.Now = _t0.AddMinutes(2);
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(c, email)).StatusCode);
        var later = await LinkAsync(email);
        Assert.NotNull(later);
        Assert.True((await CompleteAsync(c, proof)).IsSuccessStatusCode);
        Assert.Equal(1, Users(email));

        // The account exists: no link of that registration opens a second form.
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyAsync(c, later!)).StatusCode);
        Assert.Equal(0, _f.WithDb(db => db.PendingRegistrationLinks.Count()));
    }

    // ---- 14: everyone else ----------------------------------------------------------------------------

    [Fact]
    public async Task Existing_accounts_sign_in_as_before_and_the_one_step_form_is_closed()
    {
        Start();
        var admin = _f.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _f.AdminPassword));
        Assert.True((await admin.GetAsync("/api/me/profile")).IsSuccessStatusCode);

        var classic = await _f.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            firstName = "A", lastName = "B", phone = TestPhones.Next(), email = NewEmail(),
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });
        Assert.Equal(HttpStatusCode.Conflict, classic.StatusCode);

        var config = await _f.CreateClient().GetFromJsonAsync<JsonElement>("/api/config");
        Assert.Equal("email-first", config.GetProperty("registrationFlow").GetString());
    }

    [Fact]
    public async Task Off_unless_switched_on()
    {
        Start(emailFirst: false);
        Assert.Equal(HttpStatusCode.NotFound, (await StartAsync(_f.CreateClient(), NewEmail())).StatusCode);
        Assert.Equal("classic", (await _f.CreateClient().GetFromJsonAsync<JsonElement>("/api/config")).GetProperty("registrationFlow").GetString());
    }

    // ---- 15: the phone rule ---------------------------------------------------------------------------

    [Fact]
    public async Task The_phone_rule_holds_and_a_refused_form_keeps_the_proof_usable()
    {
        Start();
        var c = _f.CreateClient();
        var email = NewEmail();
        var proof = await ProofAsync(c, email);
        var landline = await CompleteAsync(c, proof, phone: "+994 12 444 55 66");
        Assert.Equal(HttpStatusCode.BadRequest, landline.StatusCode);
        Assert.Contains("Azərbaycan mobil nömrəsini yazın", await landline.Content.ReadAsStringAsync());
        Assert.Equal(0, Users(email));
        Assert.True((await CompleteAsync(c, proof)).IsSuccessStatusCode);
        Assert.Equal(1, Users(email));
    }

    // ---- helpers for the vote test ---------------------------------------------------------------------

    private async Task<string> SubmitAsync(HttpClient client)
    {
        static async Task<JsonElement> Json(HttpResponseMessage r)
        {
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
            return await r.Content.ReadFromJsonAsync<JsonElement>();
        }
        var id = (await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "balaxani" }))).GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title = "Balaxanı şevronu", concept = "K", symbols = "S" } }));
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "eskiz.png");
        await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        var reference = (await Json(await client.PostAsync($"/api/submissions/{id}/submit", null))).GetProperty("referenceNumber").GetString()!;
        await _f.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync(x => x.Slug == Chevron);
            c.VotingStartsAt = _t0.AddSeconds(-5);
            c.VotingEndsAt = _t0.AddHours(2);
            await db.SaveChangesAsync();
        });
        return reference;
    }
}
