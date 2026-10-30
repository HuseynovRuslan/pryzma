using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meydan.Api.Common;
using Meydan.Api.Features.Auth.Qrlog;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meydan.Api.Tests;

/// <summary>
/// Phase 6: "QRLog ilə daxil ol". QRLog vouches for a signed-in employee over a signed back channel; the browser
/// that started the ticket collects the result with its cookie. These pin the security rules: single-use codes, a
/// short signature window, constant-time checks, no account takeover by e-mail, no admin from QRLog, same-site
/// return paths only, explicit linking and unlinking.
/// </summary>
public sealed class QrlogSsoTests
{
    private const string Secret = "test-vouch-secret-0123456789abcdef-0123456789";
    private const string AuthorizeUrl = "https://app.qrlog.az/signin/meydan?code={code}&return={return}";

    private static MeydanFactory Factory(bool enabled = true, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["App:PublicUrl"] = "https://meydan.test",
            ["QrlogSso:Enabled"] = enabled ? "true" : "false",
            ["QrlogSso:VouchSecret"] = Secret,
            ["QrlogSso:AuthorizeUrl"] = AuthorizeUrl,
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return new MeydanFactory { Settings = settings };
    }

    private static HttpClient Browser(MeydanFactory f) => f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private static async Task<(string Code, string AuthorizeUrl)> StartAsync(HttpClient browser, string? returnTo = null, bool link = false)
    {
        var res = await browser.PostAsJsonAsync("/api/auth/qrlog/start", new { returnTo, link });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var url = body.GetProperty("authorizeUrl").GetString()!;
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["code"]!;
        return (code, url);
    }

    /// <summary>What QRLog's server sends: the raw JSON body, a timestamp and HMAC-SHA256(secret, timestamp + "\n" + body).</summary>
    private static async Task<HttpResponseMessage> ConfirmAsync(MeydanFactory f, object payload, string secret = Secret,
        string? timestamp = null, string? signature = null)
    {
        var body = payload as string ?? JsonSerializer.Serialize(payload);
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToString("O");
        var sig = signature ?? Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + "\n" + body))).ToLowerInvariant();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/qrlog/confirm") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add(QrlogSsoController.TimestampHeader, ts);
        req.Headers.Add(QrlogSsoController.SignatureHeader, sig);
        // A separate client: QRLog's server has none of the browser's cookies.
        return await f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }).SendAsync(req);
    }

    private static object Vouch(string code, Guid subject, string first = "Leyla", string last = "Əsgərova", string? email = "leyla@example.com") =>
        new { code, subject, fullName = $"{first} {last}", firstName = first, lastName = last, email, emailVerified = false };

    private static async Task<JsonElement> ClaimAsync(HttpClient browser)
    {
        var res = await browser.PostAsync("/api/auth/qrlog/claim", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_qrlog_login_creates_a_participant_account_signs_in_once_and_keeps_the_return_path()
    {
        using var f = Factory();
        var browser = Browser(f);
        var (code, url) = await StartAsync(browser, "/competitions/baki-kendleri-sevron-dizayni/submit?category=mastaga");
        Assert.StartsWith("https://app.qrlog.az/signin/meydan?code=", url);
        Assert.Contains("return=https%3A%2F%2Fmeydan.test%2Flogin%2Fqrlog", url);
        Assert.Equal(32, code.Length);

        Assert.Equal("pending", (await ClaimAsync(browser)).GetProperty("status").GetString());

        var subject = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(code, subject))).StatusCode);

        var claim = await ClaimAsync(browser);
        Assert.Equal("signed-in", claim.GetProperty("status").GetString());
        Assert.Equal("/competitions/baki-kendleri-sevron-dizayni/submit?category=mastaga", claim.GetProperty("returnTo").GetString());
        var session = claim.GetProperty("session");
        var user = session.GetProperty("user");
        Assert.Equal("Leyla", user.GetProperty("firstName").GetString());
        Assert.Equal("Əsgərova", user.GetProperty("lastName").GetString());
        Assert.Equal("leyla@example.com", user.GetProperty("email").GetString());
        Assert.Equal(["Participant"], user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.True(user.GetProperty("phone").ValueKind == JsonValueKind.Null); // nothing copied that MEYDAN does not need

        // The session is a real one: access token works, refresh cookie was set.
        browser.UseToken(session.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/me/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/api/auth/refresh", null)).StatusCode);

        // Once. The cookie is gone and the ticket is spent: nothing more to collect, and its identity is wiped.
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/api/auth/qrlog/claim", null)).StatusCode);
        var ticket = f.WithDb(db => db.QrlogLoginTickets.Single(t => t.Code == code));
        Assert.Equal(QrlogTicketStatus.Claimed, ticket.Status);
        Assert.Null(ticket.Subject);
        Assert.Null(ticket.Email);
        Assert.Null(ticket.FirstName);

        var stored = f.WithDb(db => db.Users.Single(u => u.Email == "leyla@example.com"));
        Assert.False(stored.EmailConfirmed);
        Assert.Null(stored.PasswordHash);
        Assert.Equal(1, f.WithDb(db => db.UserLogins.Count(l => l.LoginProvider == "QRLog" && l.ProviderKey == subject.ToString() && l.UserId == stored.Id)));
    }

    [Fact]
    public async Task The_same_qrlog_identity_always_lands_on_the_same_account_even_with_no_e_mail()
    {
        using var f = Factory();
        var subject = Guid.NewGuid();
        for (var i = 0; i < 2; i++)
        {
            var browser = Browser(f);
            var (code, _) = await StartAsync(browser);
            Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(code, subject, "Rəşad", "Quliyev", email: null))).StatusCode);
            var claim = await ClaimAsync(browser);
            Assert.Equal("signed-in", claim.GetProperty("status").GetString());
            Assert.Equal("", claim.GetProperty("session").GetProperty("user").GetProperty("email").GetString());
        }
        Assert.Equal(1, f.WithDb(db => db.UserLogins.Count(l => l.ProviderKey == subject.ToString())));
        Assert.Equal(1, f.WithDb(db => db.Users.Count(u => u.UserName == $"qrlog-{subject}")));
    }

    [Fact]
    public async Task A_confirmation_with_a_wrong_secret_a_forged_signature_a_stale_timestamp_or_an_unknown_code_is_refused()
    {
        using var f = Factory();
        var browser = Browser(f);
        var (code, _) = await StartAsync(browser);
        var subject = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await ConfirmAsync(f, Vouch(code, subject), secret: "another-app-secret-0123456789abcdef-xyz")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ConfirmAsync(f, Vouch(code, subject), signature: new string('0', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ConfirmAsync(f, Vouch(code, subject), timestamp: DateTimeOffset.UtcNow.AddMinutes(-6).ToString("O"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ConfirmAsync(f, Vouch(code, subject), timestamp: "not-a-date")).StatusCode);
        // A signature over a different body (tampered payload) does not carry over.
        var goodTs = DateTimeOffset.UtcNow.ToString("O");
        var otherBody = JsonSerializer.Serialize(Vouch(code, Guid.NewGuid()));
        var sigForOther = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(goodTs + "\n" + otherBody))).ToLowerInvariant();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ConfirmAsync(f, Vouch(code, subject), timestamp: goodTs, signature: sigForOther)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ConfirmAsync(f, Vouch("00000000000000000000000000000000", subject))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ConfirmAsync(f, new { code, subject = "not-a-guid", fullName = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmAsync(f, "not json at all")).StatusCode);

        // Nothing above touched the ticket; the real confirmation still works.
        Assert.Equal("pending", (await ClaimAsync(browser)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(code, subject))).StatusCode);
        Assert.Equal("signed-in", (await ClaimAsync(browser)).GetProperty("status").GetString());
        Assert.DoesNotContain(f.Logs.Lines, l => l.Contains(Secret));
    }

    [Fact]
    public async Task A_code_is_single_use_a_second_confirmation_is_refused_and_the_first_identity_stands()
    {
        using var f = Factory();
        var browser = Browser(f);
        var (code, _) = await StartAsync(browser);
        var first = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(code, first, "Birinci", "Şəxs", "first@example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ConfirmAsync(f, Vouch(code, Guid.NewGuid(), "İkinci", "Şəxs", "second@example.com"))).StatusCode);

        var claim = await ClaimAsync(browser);
        Assert.Equal("first@example.com", claim.GetProperty("session").GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await ConfirmAsync(f, Vouch(code, first))).StatusCode); // replay after use
        Assert.Equal(0, f.WithDb(db => db.Users.Count(u => u.Email == "second@example.com")));
    }

    [Fact]
    public async Task Only_the_browser_that_started_the_ticket_can_collect_it()
    {
        using var f = Factory();
        var browser = Browser(f);
        var (code, _) = await StartAsync(browser);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(code, Guid.NewGuid()))).StatusCode);

        // Another browser knowing the code (it was in a URL) but not the cookie secret.
        var stranger = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync("/api/auth/qrlog/claim", null)).StatusCode);
        var forged = new HttpRequestMessage(HttpMethod.Post, "/api/auth/qrlog/claim");
        forged.Headers.Add("Cookie", $"{QrlogSsoController.CookieName}={code}.{new string('A', 43)}");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(forged)).StatusCode);
        // A cookie from a different ticket (wrong state) is just as useless.
        var other = Browser(f);
        await StartAsync(other);
        Assert.Equal("pending", (await ClaimAsync(other)).GetProperty("status").GetString());

        // The rightful browser still gets it.
        Assert.Equal("signed-in", (await ClaimAsync(browser)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_expired_ticket_can_neither_be_confirmed_nor_collected()
    {
        using var f = Factory();
        var browser = Browser(f);
        var (code, _) = await StartAsync(browser);
        f.Clock.Now = f.Clock.Now.AddMinutes(6);
        Assert.Equal(HttpStatusCode.Conflict, (await ConfirmAsync(f, Vouch(code, Guid.NewGuid()), timestamp: f.Clock.Now.ToString("O"))).StatusCode);
        Assert.Equal("gone", (await ClaimAsync(browser)).GetProperty("status").GetString());
        Assert.Equal(0, f.WithDb(db => db.UserLogins.Count()));
    }

    [Fact]
    public async Task A_cancelled_ticket_is_dead_for_qrlog_and_for_the_browser()
    {
        using var f = Factory();
        var browser = Browser(f);
        var (code, _) = await StartAsync(browser);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/api/auth/qrlog/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ConfirmAsync(f, Vouch(code, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/api/auth/qrlog/claim", null)).StatusCode);
        Assert.Equal(QrlogTicketStatus.Ended, f.WithDb(db => db.QrlogLoginTickets.Single(t => t.Code == code).Status));
    }

    [Theory]
    [InlineData("https://evil.example/phish", "/")]
    [InlineData("//evil.example/phish", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/my/submissions", "/my/submissions")]
    [InlineData(null, "/")]
    public async Task Return_paths_are_same_site_or_nothing(string? given, string expected)
    {
        using var f = Factory();
        var browser = Browser(f);
        var (code, _) = await StartAsync(browser, given);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(code, Guid.NewGuid()))).StatusCode);
        Assert.Equal(expected, (await ClaimAsync(browser)).GetProperty("returnTo").GetString());
    }

    [Fact]
    public async Task Whatever_role_qrlog_claims_a_qrlog_user_is_never_a_meydan_admin()
    {
        using var f = Factory();
        var browser = Browser(f);
        var (code, _) = await StartAsync(browser);
        var payload = new { code, subject = Guid.NewGuid(), fullName = "Boss Adam", email = "boss@example.com", emailVerified = true, role = "Admin", roles = new[] { "Admin" }, tenantId = Guid.NewGuid(), phoneNumber = "+994500000000" };
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, payload)).StatusCode);
        var session = (await ClaimAsync(browser)).GetProperty("session");
        var user = session.GetProperty("user");
        Assert.Equal(["Participant"], user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.True(user.GetProperty("phone").ValueKind == JsonValueKind.Null);
        Assert.False(f.WithDb(db => db.Users.Single(u => u.Email == "boss@example.com").EmailConfirmed));
        // Admin endpoints stay closed to that session.
        browser.UseToken(session.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/admin/projects")).StatusCode);
    }

    [Fact]
    public async Task An_admin_account_can_neither_link_qrlog_nor_be_reached_through_a_qrlog_identity()
    {
        using var f = Factory();
        var admin = Browser(f);
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, f.AdminPassword));

        // Linking from the admin's own session is refused before any ticket exists.
        var link = await admin.PostAsJsonAsync("/api/auth/qrlog/start", new { link = true });
        Assert.Equal(HttpStatusCode.Forbidden, link.StatusCode);
        Assert.Contains("E-mail və şifrə", (await link.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(0, f.WithDb(db => db.QrlogLoginTickets.Count()));

        // Even with a QRLog identity attached to the admin account by other means (a participant linked earlier and
        // promoted later, or a row written by hand), a QRLog login never opens an admin session.
        var subject = Guid.NewGuid();
        using (var scope = f.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var adminUser = await users.FindByEmailAsync(MeydanFactory.AdminEmail);
            Assert.True((await users.AddLoginAsync(adminUser!, new UserLoginInfo("QRLog", subject.ToString(), "QRLog"))).Succeeded);
        }
        var browser = Browser(f);
        var (code, _) = await StartAsync(browser);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(code, subject, "Admin", "MEYDAN", MeydanFactory.AdminEmail))).StatusCode);
        var claim = await browser.PostAsync("/api/auth/qrlog/claim", null);
        Assert.Equal(HttpStatusCode.Forbidden, claim.StatusCode);
        Assert.DoesNotContain("accessToken", await claim.Content.ReadAsStringAsync());
        Assert.Equal(QrlogTicketStatus.Claimed, f.WithDb(db => db.QrlogLoginTickets.Single(t => t.Code == code).Status)); // spent
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/auth/refresh", null)).StatusCode); // no session cookie
    }

    [Fact]
    public async Task An_existing_account_with_the_same_e_mail_is_never_taken_over_but_can_be_linked_explicitly()
    {
        using var f = Factory();
        var local = Browser(f);
        var localToken = await local.RegisterAsync("aysel@example.com", "localpass123");
        var usersBefore = f.WithDb(db => db.Users.Count());

        // QRLog vouches for somebody whose record carries that address.
        var browser = Browser(f);
        var (code, _) = await StartAsync(browser);
        var subject = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(code, subject, "Aysel", "Məmmədova", "aysel@example.com"))).StatusCode);
        var claim = await ClaimAsync(browser);
        Assert.Equal("email-taken", claim.GetProperty("status").GetString());
        Assert.Equal("aysel@example.com", claim.GetProperty("email").GetString());
        Assert.False(claim.TryGetProperty("session", out var s) && s.ValueKind != JsonValueKind.Null);
        Assert.Equal(usersBefore, f.WithDb(db => db.Users.Count()));
        Assert.Equal(0, f.WithDb(db => db.UserLogins.Count()));

        // The rightful owner signs in with the password and links from the profile.
        local.UseToken(localToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Browser(f).PostAsJsonAsync("/api/auth/qrlog/start", new { link = true })).StatusCode);
        var (linkCode, _) = await StartAsync(local, "/my/profile", link: true);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(linkCode, subject, "Aysel", "Məmmədova", "aysel@example.com"))).StatusCode);
        var linked = await ClaimAsync(local);
        Assert.Equal("linked", linked.GetProperty("status").GetString());
        Assert.Equal("/my/profile", linked.GetProperty("returnTo").GetString());
        var logins = await (await local.GetAsync("/api/me/logins")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(logins.GetProperty("hasPassword").GetBoolean());
        Assert.True(logins.GetProperty("qrlog").GetBoolean());
        Assert.Equal(1, f.WithDb(db => db.AuditLogs.Count(a => a.Action == "QrlogLinked" && a.EntityType == "Account")));

        // From now on QRLog lands on that very account.
        var again = Browser(f);
        var (code2, _) = await StartAsync(again);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(code2, subject, "Aysel", "Məmmədova", "aysel@example.com"))).StatusCode);
        var claim2 = await ClaimAsync(again);
        Assert.Equal("signed-in", claim2.GetProperty("status").GetString());
        Assert.Equal("aysel@example.com", claim2.GetProperty("session").GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(usersBefore, f.WithDb(db => db.Users.Count()));
    }

    [Fact]
    public async Task Linking_needs_the_same_signed_in_account_and_a_qrlog_identity_nobody_else_holds()
    {
        using var f = Factory();
        var alice = Browser(f);
        alice.UseToken(await alice.RegisterAsync("alice@example.com"));
        var bob = Browser(f);
        bob.UseToken(await bob.RegisterAsync("bob@example.com"));
        var subject = Guid.NewGuid();

        // Alice's link ticket, collected by Bob's session (he got hold of Alice's cookie jar): refused.
        var (aliceCode, _) = await StartAsync(alice, link: true);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(aliceCode, subject, email: null))).StatusCode);
        var cookie = f.WithDb(db => db.QrlogLoginTickets.Single(t => t.Code == aliceCode)); // Bob would need the secret too — simulate by reusing Alice's client with Bob's token
        alice.UseToken(await bob.LoginAsync("bob@example.com", "secret123"));
        var wrongUser = await alice.PostAsync("/api/auth/qrlog/claim", null);
        Assert.Equal(HttpStatusCode.Forbidden, wrongUser.StatusCode);
        Assert.Equal(0, f.WithDb(db => db.UserLogins.Count()));
        Assert.Equal(QrlogTicketStatus.Claimed, f.WithDb(db => db.QrlogLoginTickets.Single(t => t.Code == aliceCode).Status)); // spent either way

        // Alice links properly.
        alice.UseToken(await alice.LoginAsync("alice@example.com", "secret123"));
        var (code2, _) = await StartAsync(alice, link: true);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(code2, subject, email: null))).StatusCode);
        Assert.Equal("linked", (await ClaimAsync(alice)).GetProperty("status").GetString());

        // Bob cannot link the same QRLog identity.
        var (bobCode, _) = await StartAsync(bob, link: true);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(bobCode, subject, email: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsync("/api/auth/qrlog/claim", null)).StatusCode);
        Assert.Equal(1, f.WithDb(db => db.UserLogins.Count()));
    }

    [Fact]
    public async Task Unlinking_needs_a_password_and_is_audited()
    {
        using var f = Factory();
        // An account that exists only through QRLog cannot drop its only way in.
        var browser = Browser(f);
        var (code, _) = await StartAsync(browser);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(code, Guid.NewGuid(), "Only", "Qrlog", "only@example.com"))).StatusCode);
        browser.UseToken((await ClaimAsync(browser)).GetProperty("session").GetProperty("accessToken").GetString()!);
        var logins = await (await browser.GetAsync("/api/me/logins")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(logins.GetProperty("hasPassword").GetBoolean());
        Assert.True(logins.GetProperty("qrlog").GetBoolean());
        var refused = await browser.DeleteAsync("/api/me/logins/qrlog");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("şifrə", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());

        // A password account that linked QRLog can unlink it.
        var local = Browser(f);
        local.UseToken(await local.RegisterAsync("pw@example.com"));
        var (linkCode, _) = await StartAsync(local, link: true);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(f, Vouch(linkCode, Guid.NewGuid(), email: null))).StatusCode);
        Assert.Equal("linked", (await ClaimAsync(local)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await local.DeleteAsync("/api/me/logins/qrlog")).StatusCode);
        // Removing a sign-in method rotates the security stamp: the access token in hand is refused and the SPA
        // refreshes once (the refresh cookie is still good) — the test does the same by hand.
        Assert.Equal(HttpStatusCode.Unauthorized, (await local.GetAsync("/api/me/logins")).StatusCode);
        var refreshed = await local.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        local.UseToken((await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);
        Assert.False((await (await local.GetAsync("/api/me/logins")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("qrlog").GetBoolean());
        Assert.Equal(1, f.WithDb(db => db.AuditLogs.Count(a => a.Action == "QrlogUnlinked")));
        Assert.Equal(HttpStatusCode.NoContent, (await local.DeleteAsync("/api/me/logins/qrlog")).StatusCode); // idempotent
    }

    [Fact]
    public async Task When_the_provider_is_off_nothing_answers_and_the_config_says_so()
    {
        using var f = Factory(enabled: false);
        var browser = Browser(f);
        var config = await (await browser.GetAsync("/api/config")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(config.GetProperty("qrlogLogin").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsJsonAsync("/api/auth/qrlog/start", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await ConfirmAsync(f, Vouch("0123456789abcdef0123456789abcdef", Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/api/auth/qrlog/claim", null)).StatusCode);

        using var on = Factory(extra: new() { ["Email:Mode"] = "Disabled" });
        var config2 = await (await Browser(on).GetAsync("/api/config")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(config2.GetProperty("qrlogLogin").GetBoolean());
        Assert.False(config2.GetProperty("passwordReset").GetBoolean());
    }

    [Fact]
    public void Production_refuses_to_start_with_the_provider_half_configured()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["QrlogSso:Enabled"] = "true",
            ["QrlogSso:VouchSecret"] = "hunter2-value",
            ["QrlogSso:AuthorizeUrl"] = "http://app.qrlog.az/signin/meydan?code={code}",
        }).Build();
        var problems = ProductionConfiguration.Check(config, Path.GetTempPath());
        Assert.Contains(problems, p => p.Contains("QrlogSso:VouchSecret"));
        Assert.Contains(problems, p => p.Contains("QrlogSso:AuthorizeUrl"));
        Assert.DoesNotContain(problems, p => p.Contains("hunter2")); // the value itself never appears
        Assert.True(QrlogSsoOptions.IsValidAuthorizeUrl(AuthorizeUrl));
        Assert.False(QrlogSsoOptions.IsValidAuthorizeUrl("https://evil.example/signin?code={code}&return={return}"));
    }
}
