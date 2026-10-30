using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Meydan.Api.Tests;

public sealed class AuthTests : IDisposable
{
    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    [Fact]
    public async Task Register_returns_token_sets_httponly_refresh_cookie_and_participant_role()
    {
        var client = NewClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = "Aysel", lastName = "Məmmədova", phone = TestPhones.Next(),
            email = "aysel@example.com", password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var cookie = Assert.Single(res.Headers.GetValues("Set-Cookie"));
        Assert.Contains("meydan_rt=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/auth", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires", cookie, StringComparison.OrdinalIgnoreCase); // session login

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.Equal(["Participant"], body.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Register_validation_errors_are_keyed_by_camelCase_field_in_azerbaijani()
    {
        var res = await NewClient().PostAsJsonAsync("/api/auth/register", new
        {
            firstName = "", lastName = "X", phone = "abc", email = "not-an-email",
            password = "secret123", confirmPassword = "different1", acceptTerms = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var errors = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("Adınızı yazın.", errors.GetProperty("firstName")[0].GetString());
        Assert.Equal("Şifrələr üst-üstə düşmür.", errors.GetProperty("confirmPassword")[0].GetString());
        Assert.True(errors.TryGetProperty("acceptTerms", out _));
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("phone", out _));
    }

    [Fact]
    public async Task Duplicate_email_is_rejected_on_the_email_field()
    {
        await NewClient().RegisterAsync("dup@example.com");
        var res = await NewClient().PostAsJsonAsync("/api/auth/register", new
        {
            firstName = "A", lastName = "B", phone = TestPhones.Next(), email = "dup@example.com",
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var errors = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("Bu e-mail ilə artıq hesab var.", errors.GetProperty("email")[0].GetString());
    }

    [Fact]
    public async Task Weak_password_is_rejected_with_identity_rules()
    {
        var res = await NewClient().PostAsJsonAsync("/api/auth/register", new
        {
            firstName = "A", lastName = "B", phone = TestPhones.Next(), email = "weak@example.com",
            password = "abcdefgh", confirmPassword = "abcdefgh", acceptTerms = true,
        });
        var errors = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("Şifrədə ən azı bir rəqəm olmalıdır.", errors.GetProperty("password")[0].GetString());
    }

    [Fact]
    public async Task A_refused_sign_in_says_whether_there_is_no_account_a_wrong_password_or_only_qrlog()
    {
        async Task<(HttpStatusCode Status, string? Title)> Login(string email, string password)
        {
            var res = await NewClient().PostAsJsonAsync("/api/auth/login", new { email, password, rememberMe = false });
            return (res.StatusCode, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        }

        await NewClient().RegisterAsync("login@example.com");
        Assert.Equal((HttpStatusCode.Unauthorized, "Bu e-mail ilə hesab yoxdur. Əvvəlcə qeydiyyatdan keçin."), await Login("nobody@example.com", "secret123"));
        Assert.Equal((HttpStatusCode.Unauthorized, "Şifrə yanlışdır. Unutmusunuzsa, «Şifrəni unutmuşam» ilə yeniləyin."), await Login("LOGIN@example.com", "wrong-pass1"));

        // An account made through QRLog has no password; trying one is not counted against it.
        await _factory.WithDbAsync(async db =>
        {
            db.Users.Add(new Meydan.Domain.Entities.ApplicationUser
            {
                UserName = "qr@example.com", Email = "qr@example.com", NormalizedEmail = "QR@EXAMPLE.COM", NormalizedUserName = "QR@EXAMPLE.COM",
                FirstName = "Q", LastName = "R", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                SecurityStamp = Guid.NewGuid().ToString(), LockoutEnabled = true,
            });
            await db.SaveChangesAsync();
        });
        for (var i = 0; i < 6; i++)
            Assert.Equal((HttpStatusCode.Unauthorized, "Bu hesab QRLog ilə açılıb və şifrəsi yoxdur. «QRLog ilə daxil ol» düyməsini seçin."), await Login("qr@example.com", "anything1"));
        Assert.Equal(0, _factory.WithDb(db => db.Users.Single(u => u.NormalizedEmail == "QR@EXAMPLE.COM").AccessFailedCount));
    }

    [Fact]
    public async Task Five_failed_logins_lock_the_account()
    {
        await NewClient().RegisterAsync("lock@example.com");
        var client = NewClient();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/auth/login", new { email = "lock@example.com", password = "nope12345" });

        var res = await client.PostAsJsonAsync("/api/auth/login", new { email = "lock@example.com", password = "secret123" });
        Assert.Equal(HttpStatusCode.Locked, res.StatusCode);
    }

    [Fact]
    public async Task Remember_me_issues_a_persistent_cookie()
    {
        await NewClient().RegisterAsync("remember@example.com");
        var res = await NewClient().PostAsJsonAsync("/api/auth/login",
            new { email = "remember@example.com", password = "secret123", rememberMe = true });
        var cookie = Assert.Single(res.Headers.GetValues("Set-Cookie"));
        Assert.Contains("expires", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_rotates_the_cookie_and_restores_the_session()
    {
        var client = NewClient();
        await client.RegisterAsync("refresh@example.com");

        var res = await client.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refresh@example.com", body.GetProperty("user").GetProperty("email").GetString());

        client.UseToken(body.GetProperty("accessToken").GetString()!);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("Aysel", me.GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_after_the_grace_window_revokes_the_family()
    {
        var client = NewClient();
        await client.RegisterAsync("theft@example.com");
        var stolen = ExtractCookie(await client.PostAsync("/api/auth/refresh", null)); // token #2
        await client.PostAsync("/api/auth/refresh", null); // rotates #2 → #3 inside the client jar

        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(2);

        var attacker = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var replay = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        replay.Headers.Add("Cookie", $"meydan_rt={stolen}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.SendAsync(replay)).StatusCode);

        // The legitimate holder of the newest token is signed out as well.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_refresh_within_grace_window_does_not_log_the_user_out()
    {
        var client = NewClient();
        await client.RegisterAsync("tabs@example.com");
        var token = ExtractCookie(await client.PostAsync("/api/auth/refresh", null));
        await client.PostAsync("/api/auth/refresh", null); // tab A rotates

        var tabB = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        req.Headers.Add("Cookie", $"meydan_rt={token}");
        Assert.Equal(HttpStatusCode.OK, (await tabB.SendAsync(req)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var client = NewClient();
        await client.RegisterAsync("logout@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected()
    {
        var client = NewClient();
        await client.RegisterAsync("expire@example.com");
        _factory.Clock.Now = _factory.Clock.Now.AddHours(13); // session logins live 12h
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Forgot_password_does_not_reveal_whether_an_account_exists()
    {
        var res = await NewClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = "nobody@example.com" });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task Reset_password_with_garbage_token_fails_on_token_field()
    {
        await NewClient().RegisterAsync("reset@example.com");
        var res = await NewClient().PostAsJsonAsync("/api/auth/reset-password", new
        {
            email = "reset@example.com", token = "bm9wZQ", password = "newpass123", confirmPassword = "newpass123",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var errors = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task Access_tokens_carry_the_full_sha256_of_the_security_stamp_never_the_stamp_itself()
    {
        var client = NewClient();
        var token = await client.RegisterAsync("stamp@example.com");
        var stamp = _factory.WithDb(db => db.Users.Single(u => u.Email == "stamp@example.com").SecurityStamp)!;

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var sst = jwt.Claims.Single(c => c.Type == AccessTokenService.StampClaim).Value;
        Assert.Equal(43, sst.Length);
        var digest = Base64UrlEncoder.DecodeBytes(sst);
        Assert.Equal(32, digest.Length); // 256 bits: 128-bit collision resistance, nothing truncated
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)), digest);
        Assert.DoesNotContain(stamp, token);
        Assert.DoesNotContain(stamp, Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(token.Split('.')[1])));
    }

    [Fact]
    public async Task Only_the_exact_current_stamp_fingerprint_is_accepted_even_with_a_valid_signature()
    {
        var client = NewClient();
        await client.RegisterAsync("forged@example.com");
        var user = _factory.WithDb(db => db.Users.Single(u => u.Email == "forged@example.com"));
        var fingerprint = AccessTokenService.StampFingerprint(user.SecurityStamp);
        var jwt = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

        string Mint(string sst)
        {
            var now = DateTime.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer: jwt.Issuer, audience: jwt.Audience,
                claims: [new(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new(AccessTokenService.StampClaim, sst),
                         new(ClaimTypes.Role, "Participant")],
                notBefore: now, expires: now.AddMinutes(5),
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    SecurityAlgorithms.HmacSha256)));
        }
        async Task<HttpStatusCode> Profile(string token)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/me/profile");
            req.Headers.Authorization = new("Bearer", token);
            return (await _factory.CreateClient().SendAsync(req)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Profile(Mint(fingerprint)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Profile(Mint(fingerprint[..22])));  // a prefix is not enough
        Assert.Equal(HttpStatusCode.Unauthorized, await Profile(Mint(fingerprint[..42] + (fingerprint[42] == 'A' ? 'B' : 'A'))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Profile(Mint(AccessTokenService.StampFingerprint("another-stamp"))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Profile(Mint(user.SecurityStamp!))); // the raw stamp is not a fingerprint
    }

    private static string ExtractCookie(HttpResponseMessage res)
    {
        var header = res.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("meydan_rt=", StringComparison.Ordinal));
        return header["meydan_rt=".Length..header.IndexOf(';')];
    }
}
