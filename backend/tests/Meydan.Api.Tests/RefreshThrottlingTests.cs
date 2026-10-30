using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Meydan.Api.Tests;

/// <summary>
/// The session refresh with the PRODUCTION limits (appsettings.json; the factory's "unlimited" test values are not applied). Every
/// request from a test host comes from one client address — the shared-address case: an office NAT, a mobile
/// carrier's CGNAT, public Wi-Fi. A session is throttled on its own, never by the people it shares an address with;
/// junk cookies cannot buy unlimited lookups; and a 429 never signs anyone out.
/// </summary>
public sealed class RefreshThrottlingTests : IDisposable
{
    private const string CurrentForm = "^[0-9a-f]{32}\\.[A-Za-z0-9_-]{64}$";

    private readonly MeydanFactory _factory = new() { ProductionSessionLimits = true };

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Many_people_behind_one_address_keep_refreshing_past_the_old_per_address_limit()
    {
        // 25 participants on one office connection, each restoring their session on three page loads: 75 refreshes
        // in a minute from one address. The old policy (60 a minute per address) refused the 61st.
        var people = new List<HttpClient>();
        for (var i = 0; i < 25; i++)
        {
            var person = Browser();
            await person.RegisterAsync($"office{i}@example.com");
            people.Add(person);
        }

        var answers = new List<HttpStatusCode>();
        for (var round = 0; round < 3; round++)
            foreach (var person in people)
                answers.Add((await Refresh(person)).StatusCode);

        Assert.Equal(75, answers.Count(a => a == HttpStatusCode.OK));
    }

    [Fact]
    public async Task A_session_hammering_refresh_is_throttled_alone_and_stays_signed_in()
    {
        var hammer = Browser();
        await hammer.RegisterAsync("hammer@example.com");
        var neighbour = Browser();
        await neighbour.RegisterAsync("neighbour@example.com");

        // Like a browser, the hammer follows every rotation: a new cookie value each time, the same session.
        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.OK, (await Refresh(hammer)).StatusCode);
        var throttled = await Refresh(hammer);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.InRange(throttled.Headers.RetryAfter!.Delta!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.False(throttled.Headers.Contains("Set-Cookie"));

        // The neighbour on the same address refreshes as usual.
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await Refresh(neighbour)).StatusCode);

        // Throttling revoked nothing: the hammer's newest token is live and its family intact.
        Assert.Equal(1, LiveTokens("hammer@example.com"));
        Assert.Equal(1, LiveTokens("neighbour@example.com"));
    }

    [Fact]
    public async Task Junk_cookies_cannot_buy_unlimited_lookups_and_the_guard_never_signs_anyone_out()
    {
        var resident = Browser();
        await resident.RegisterAsync("resident@example.com");
        var attacker = Bare();

        // A fresh, well-formed value every time — a new "session" each time, so only the address guard stops it.
        var answers = new List<HttpStatusCode>();
        for (var i = 0; i < 70; i++)
            answers.Add((await RefreshWith(attacker, CurrentFormJunk())).StatusCode);
        Assert.Equal(60, answers.Count(a => a == HttpStatusCode.Unauthorized));   // 60 lookups that matched nothing
        Assert.Equal(10, answers.Count(a => a == HttpStatusCode.TooManyRequests)); // then refused before any lookup
        Assert.Equal(HttpStatusCode.TooManyRequests, answers[^1]);

        // While that address is refused, a resident behind it waits too — but is never signed out: 429, not 401,
        // the cookie untouched, the session live.
        var waiting = await Refresh(resident);
        Assert.Equal(HttpStatusCode.TooManyRequests, waiting.StatusCode);
        Assert.False(waiting.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, LiveTokens("resident@example.com"));
    }

    [Fact]
    public async Task Malformed_cookies_are_refused_without_a_lookup_and_leave_the_address_budget_alone()
    {
        var resident = Browser();
        await resident.RegisterAsync("resident2@example.com");
        var junk = Bare();

        // Including a would-be token with an encoded newline after it (cookie values are URL-decoded).
        var answers = new List<HttpStatusCode> { (await RefreshWith(junk, CurrentFormJunk() + "%0A")).StatusCode };
        for (var i = 1; i < 35; i++)
            answers.Add((await RefreshWith(junk, $"not-a-token-{i}")).StatusCode);
        Assert.Equal(30, answers.Count(a => a == HttpStatusCode.Unauthorized));
        Assert.Equal(5, answers.Count(a => a == HttpStatusCode.TooManyRequests));

        // They never reached the lookup, so the address's failed-lookup budget is untouched.
        Assert.Equal(HttpStatusCode.OK, (await Refresh(resident)).StatusCode);
    }

    [Fact]
    public async Task Visitors_without_a_session_behind_one_address_are_not_throttled_at_ordinary_volume()
    {
        // Every anonymous page load asks once; answered at once with 401, no database work.
        var visitors = Bare();
        var answers = new List<HttpStatusCode>();
        for (var i = 0; i < 150; i++)
            answers.Add((await visitors.PostAsync("/api/auth/refresh", null)).StatusCode);
        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Unauthorized, a));
    }

    [Fact]
    public async Task The_throttling_answer_names_no_token_session_or_account()
    {
        var client = Browser();
        var registered = await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = "Aysel", lastName = "Məmmədova", phone = TestPhones.Next(),
            email = "quiet@example.com", password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });
        var userId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("id").GetString()!;
        var cookie = "";
        for (var i = 0; i < 20; i++)
            cookie = ExtractCookie(await Refresh(client));

        var res = await Refresh(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.NotNull(res.Headers.RetryAfter?.Delta);
        Assert.False(res.Headers.Contains("Set-Cookie"));
        var body = await res.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(["status", "title", "traceId"], json.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(429, json.GetProperty("status").GetInt32());
        foreach (var secret in new[] { cookie, cookie[..32], cookie[33..], userId, "quiet@example.com", "Aysel" })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_cookies_carry_a_stable_session_key_and_legacy_tokens_rotate_into_the_new_form()
    {
        var client = Browser();
        await client.RegisterAsync("format@example.com");
        var first = ExtractCookie(await Refresh(client));
        var second = ExtractCookie(await Refresh(client));
        Assert.Matches(CurrentForm, first);
        Assert.Matches(CurrentForm, second);
        Assert.Equal(first[..32], second[..32]); // same session
        Assert.NotEqual(first[33..], second[33..]); // new secret

        // A token issued before the family went into the cookie (the bare secret) still restores the session and
        // comes back in the new form, in the same family.
        var legacy = Base64Url(RandomNumberGenerator.GetBytes(48));
        var family = Guid.NewGuid();
        await _factory.WithDbAsync(async db =>
        {
            var userId = db.Users.Single(u => u.Email == "format@example.com").Id;
            var now = _factory.Clock.GetUtcNow();
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = userId, TokenHash = RefreshTokenService.Hash(legacy), FamilyId = family,
                CreatedAt = now, ExpiresAt = now.AddHours(1),
            });
            await db.SaveChangesAsync();
        });
        var upgraded = await RefreshWith(Bare(), legacy);
        Assert.Equal(HttpStatusCode.OK, upgraded.StatusCode);
        var renewed = ExtractCookie(upgraded);
        Assert.Matches(CurrentForm, renewed);
        Assert.StartsWith(family.ToString("N"), renewed);
    }

    [Fact]
    public async Task Unknown_revoked_and_replayed_tokens_are_still_refused_with_the_production_limits()
    {
        // Unknown (well-formed) → refused, cookie cleared.
        var stranger = await RefreshWith(Bare(), CurrentFormJunk());
        Assert.Equal(HttpStatusCode.Unauthorized, stranger.StatusCode);
        Assert.Contains(stranger.Headers.GetValues("Set-Cookie"), h => h.StartsWith("meydan_rt=;", StringComparison.Ordinal));

        // Revoked by logout → refused.
        var leaving = Browser();
        await leaving.RegisterAsync("leaving@example.com");
        var loggedOut = ExtractCookie(await Refresh(leaving));
        await leaving.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshWith(Bare(), loggedOut)).StatusCode);

        // A rotated token replayed after the grace window → the whole family is revoked, the holder signed out too.
        var victim = Browser();
        await victim.RegisterAsync("victim@example.com");
        var stolen = ExtractCookie(await Refresh(victim));
        await Refresh(victim);
        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(2);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshWith(Bare(), stolen)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(victim)).StatusCode);
        Assert.Equal(0, LiveTokens("victim@example.com"));
    }

    private HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private HttpClient Bare() => _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static Task<HttpResponseMessage> Refresh(HttpClient client) => client.PostAsync("/api/auth/refresh", null);

    private static Task<HttpResponseMessage> RefreshWith(HttpClient client, string cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        req.Headers.Add("Cookie", $"meydan_rt={cookie}");
        return client.SendAsync(req);
    }

    private int LiveTokens(string email) => _factory.WithDb(db =>
    {
        var userId = db.Users.Single(u => u.Email == email).Id;
        return db.RefreshTokens.Count(t => t.UserId == userId && t.RevokedAt == null);
    });

    private static string CurrentFormJunk() => $"{Guid.NewGuid():N}.{Base64Url(RandomNumberGenerator.GetBytes(48))}";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string ExtractCookie(HttpResponseMessage res)
    {
        var header = res.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("meydan_rt=", StringComparison.Ordinal));
        return header["meydan_rt=".Length..header.IndexOf(';')];
    }
}
