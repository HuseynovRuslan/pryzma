using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meydan.Api.Features.Auth.Qrlog;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Meydan.Api.Tests;

/// <summary>
/// "QRLog ilə daxil ol" on a desktop: the page shows a QR, a phone approves in QRLog, the desktop collects the session
/// with the cookie it was given. And on a phone: straight to QRLog and back. Both are the same ticket, the same signed
/// confirmation and the same one-time claim — only the way the code reaches QRLog differs.
/// </summary>
public sealed class QrlogQrLoginTests
{
    private const string Secret = "test-vouch-secret-0123456789abcdef-0123456789";
    private const string AuthorizeUrl = "https://app.qrlog.az/signin/meydan?code={code}&return={return}";

    private static MeydanFactory Factory(bool productionLimits = false) => new()
    {
        ProductionSessionLimits = productionLimits,
        Settings = new Dictionary<string, string?>
        {
            ["App:PublicUrl"] = "https://meydan.test",
            ["QrlogSso:Enabled"] = "true",
            ["QrlogSso:VouchSecret"] = Secret,
            ["QrlogSso:AuthorizeUrl"] = AuthorizeUrl,
        },
    };

    private static HttpClient Desktop(MeydanFactory f) => f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private static HttpClient Stranger(MeydanFactory f) => f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private sealed record Started(string Code, string AuthorizeUrl, string ScanUrl, DateTimeOffset ExpiresAt, string Cookie, string Body);

    private static async Task<Started> StartAsync(HttpClient desktop, string? returnTo = null)
    {
        var res = await desktop.PostAsJsonAsync("/api/auth/qrlog/start", new { returnTo, link = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var raw = await res.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        var scan = body.GetProperty("scanUrl").GetString()!;
        var cookie = res.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith(QrlogSsoController.CookieName + "=", StringComparison.Ordinal));
        return new Started(
            System.Web.HttpUtility.ParseQueryString(new Uri(scan).Query)["code"]!,
            body.GetProperty("authorizeUrl").GetString()!,
            scan,
            body.GetProperty("expiresAt").GetDateTimeOffset(),
            cookie[(QrlogSsoController.CookieName.Length + 1)..cookie.IndexOf(';')],
            raw);
    }

    /// <summary>What QRLog's server sends once the employee taps "Təsdiqlə" on the phone.</summary>
    private static async Task<HttpResponseMessage> PhoneApprovesAsync(MeydanFactory f, string code, Guid subject, string first = "Nigar")
    {
        var body = JsonSerializer.Serialize(new
        {
            code, subject, fullName = $"{first} Əliyeva", firstName = first, lastName = "Əliyeva", email = (string?)null, emailVerified = false,
        });
        var ts = f.Clock.GetUtcNow().ToString("O"); // QRLog's clock agrees with ours; only the ticket has aged
        var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(ts + "\n" + body))).ToLowerInvariant();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/qrlog/confirm") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add(QrlogSsoController.TimestampHeader, ts);
        req.Headers.Add(QrlogSsoController.SignatureHeader, sig);
        return await Stranger(f).SendAsync(req);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PollAsync(HttpClient desktop)
    {
        var res = await desktop.PostAsync("/api/auth/qrlog/claim", null);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    [Fact]
    public async Task The_desktop_qr_carries_only_the_ticket_code_never_the_way_back_the_claim_secret_or_a_token()
    {
        using var f = Factory();
        var started = await StartAsync(Desktop(f), "/competitions/baki-kendleri-sevron-dizayni/submit?category=bileceri");

        Assert.Matches("^[0-9a-f]{32}$", started.Code);
        Assert.Equal($"https://app.qrlog.az/signin/meydan?code={started.Code}", started.ScanUrl);
        var secret = started.Cookie[33..];
        Assert.Equal(43, secret.Length);
        Assert.DoesNotContain(secret, started.ScanUrl);
        Assert.DoesNotContain(secret, started.Body); // the claim secret travels only in the HttpOnly cookie
        Assert.DoesNotContain("return", started.ScanUrl);
        Assert.DoesNotContain("bileceri", started.ScanUrl); // nor where the person was going
        // The same-device link keeps its way back to MEYDAN's return page.
        Assert.Contains("return=https%3A%2F%2Fmeydan.test%2Flogin%2Fqrlog", started.AuthorizeUrl);
        Assert.InRange(started.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5.1));
    }

    [Fact]
    public async Task A_desktop_qr_is_pending_until_the_phone_approves_then_collected_exactly_once_where_the_person_was_going()
    {
        using var f = Factory();
        var desktop = Desktop(f);
        const string destination = "/competitions/baki-kendleri-sevron-dizayni/submit?category=bileceri";
        var started = await StartAsync(desktop, destination);

        // Polling while the phone has not approved: "pending", and nothing about anybody.
        var (status, pending) = await PollAsync(desktop);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("pending", pending.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("session").ValueKind);
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("email").ValueKind);

        // The phone scanned the QR (it only knows the code) and approved in QRLog.
        Assert.Equal(HttpStatusCode.NoContent, (await PhoneApprovesAsync(f, started.Code, Guid.NewGuid())).StatusCode);

        var (_, approved) = await PollAsync(desktop);
        Assert.Equal("signed-in", approved.GetProperty("status").GetString());
        Assert.Equal(destination, approved.GetProperty("returnTo").GetString());
        Assert.Equal(["Participant"], approved.GetProperty("session").GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());

        // Once only: the cookie is gone, the code is spent, a replayed approval is refused.
        Assert.Equal(HttpStatusCode.NotFound, (await PollAsync(desktop)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PhoneApprovesAsync(f, started.Code, Guid.NewGuid())).StatusCode);
        // The desktop is signed in for real.
        Assert.Equal(HttpStatusCode.OK, (await desktop.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task A_photo_of_the_qr_alone_cannot_collect_the_session()
    {
        using var f = Factory();
        var desktop = Desktop(f);
        var started = await StartAsync(desktop);

        // Someone photographs the screen: they have the code and nothing else.
        var thief = Stranger(f);
        Assert.Equal(HttpStatusCode.NotFound, (await thief.PostAsync("/api/auth/qrlog/claim", null)).StatusCode);
        var guess = new HttpRequestMessage(HttpMethod.Post, "/api/auth/qrlog/claim");
        guess.Headers.Add("Cookie", $"{QrlogSsoController.CookieName}={started.Code}.{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}");
        Assert.Equal(HttpStatusCode.NotFound, (await thief.SendAsync(guess)).StatusCode);

        // The owner approves on their phone; only the desktop that holds the cookie gets the session.
        Assert.Equal(HttpStatusCode.NoContent, (await PhoneApprovesAsync(f, started.Code, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await thief.SendAsync(Clone(guess))).StatusCode);
        Assert.Equal("signed-in", (await PollAsync(desktop)).Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await thief.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task An_expired_qr_can_neither_be_approved_nor_collected_and_a_new_qr_is_a_new_code()
    {
        using var f = Factory();
        var desktop = Desktop(f);
        var first = await StartAsync(desktop);
        f.Clock.Now = f.Clock.Now.AddMinutes(6);

        Assert.Equal(HttpStatusCode.Conflict, (await PhoneApprovesAsync(f, first.Code, Guid.NewGuid())).StatusCode);
        Assert.Equal("gone", (await PollAsync(desktop)).Body.GetProperty("status").GetString());

        // "Yeni QR kod yarat": a new ticket, never the old code recycled.
        var second = await StartAsync(desktop);
        Assert.NotEqual(first.Code, second.Code);
        Assert.NotEqual(first.Cookie, second.Cookie);
        Assert.Equal(HttpStatusCode.NoContent, (await PhoneApprovesAsync(f, second.Code, Guid.NewGuid())).StatusCode);
        Assert.Equal("signed-in", (await PollAsync(desktop)).Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Closing_the_qr_ends_the_ticket_so_a_late_approval_is_refused()
    {
        using var f = Factory();
        var desktop = Desktop(f);
        var started = await StartAsync(desktop);
        Assert.Equal(HttpStatusCode.NoContent, (await desktop.PostAsync("/api/auth/qrlog/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PhoneApprovesAsync(f, started.Code, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PollAsync(desktop)).Status);
    }

    [Fact]
    public async Task A_phone_goes_to_qrlog_directly_and_comes_back_signed_in_and_a_second_approval_is_refused()
    {
        using var f = Factory();
        var phone = Desktop(f);
        var started = await StartAsync(phone, "/my/submissions");

        // Same ticket, same approval page — reached by the app link instead of a scan, with the way back to MEYDAN.
        var link = new Uri(started.AuthorizeUrl);
        Assert.Equal("app.qrlog.az", link.Host);
        Assert.Equal("/signin/meydan", link.AbsolutePath);
        Assert.Equal("https://meydan.test/login/qrlog", System.Web.HttpUtility.ParseQueryString(link.Query)["return"]);

        Assert.Equal(HttpStatusCode.NoContent, (await PhoneApprovesAsync(f, started.Code, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PhoneApprovesAsync(f, started.Code, Guid.NewGuid())).StatusCode);
        var (_, back) = await PollAsync(phone);
        Assert.Equal("signed-in", back.GetProperty("status").GetString());
        Assert.Equal("/my/submissions", back.GetProperty("returnTo").GetString());
    }

    [Fact]
    public async Task Several_desktops_behind_one_address_can_wait_for_their_phones_at_once()
    {
        // Production limits. Every test request comes from one address — an office where several people sign in with
        // QRLog at the same time. The old policy allowed 90 polls a minute per address in total.
        using var f = Factory(productionLimits: true);
        var desktops = new List<HttpClient>();
        for (var i = 0; i < 6; i++)
        {
            var d = Desktop(f);
            await StartAsync(d);
            desktops.Add(d);
        }

        var answers = new List<HttpStatusCode>();
        for (var round = 0; round < 20; round++) // ~50 s of polling every 2.5 s
            foreach (var d in desktops)
                answers.Add((await PollAsync(d)).Status);
        Assert.Equal(120, answers.Count(a => a == HttpStatusCode.OK));

        // A page polling far too fast meets its own ticket's limit — nobody else's.
        var eager = desktops[0];
        var more = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++)
            more.Add((await PollAsync(eager)).Status);
        Assert.Contains(HttpStatusCode.TooManyRequests, more);
        Assert.Equal(HttpStatusCode.OK, (await PollAsync(desktops[1])).Status);
    }

    [Theory]
    [InlineData("https://app.qrlog.az/signin/meydan?code={code}&return={return}", "https://app.qrlog.az/signin/meydan?code=abc")]
    [InlineData("https://app.qrlog.az/signin/meydan?return={return}&code={code}", "https://app.qrlog.az/signin/meydan?code=abc")]
    [InlineData("https://app.qrlog.az/signin/meydan?code={code}", "https://app.qrlog.az/signin/meydan?code=abc")]
    public void The_scan_address_is_the_authorize_address_without_its_way_back(string template, string expected) =>
        Assert.Equal(expected, QrlogSsoService.ScanUrl(template, "abc"));

    [Fact]
    public async Task Baki_kendleri_has_its_real_cover_and_the_seed_never_overwrites_an_admins_cover()
    {
        using var f = Factory();
        var home = await Stranger(f).GetFromJsonAsync<JsonElement>("/api/projects/baki-kendleri");
        Assert.Equal("/images/projects/baki-kendleri-cover.png", home.GetProperty("coverImage").GetString());

        // An admin replaces it; running the seed again (as every deploy does) leaves the admin's choice alone.
        await f.WithDbAsync(async db =>
        {
            var project = await db.Projects.SingleAsync(p => p.Slug == DbSeeder.BakiProjectSlug);
            project.CoverImage = "/uploads/covers/admin-choice.webp";
            db.SeedHistory.RemoveRange(db.SeedHistory.Where(h => h.Key == "2026-09-21-baki-cover"));
            await db.SaveChangesAsync();
        });
        using (var scope = f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();
        Assert.Equal("/uploads/covers/admin-choice.webp",
            f.WithDb(db => db.Projects.Single(p => p.Slug == DbSeeder.BakiProjectSlug).CoverImage));
    }

    private static HttpRequestMessage Clone(HttpRequestMessage source)
    {
        var copy = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var h in source.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return copy;
    }
}
