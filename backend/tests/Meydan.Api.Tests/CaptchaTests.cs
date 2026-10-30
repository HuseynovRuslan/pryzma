using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Features.Auth;
using Meydan.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Tests;

/// <summary>
/// Cloudflare Turnstile's answer, read the way the server reads it — without the network: a pass, a fail, our own
/// misconfiguration, an outage, a token for another form.
/// </summary>
public sealed class TurnstileVerifierTests
{
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Forms { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Forms.Add(body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1])));
            return await answer(request);
        }
    }

    private static (TurnstileVerifier, Stub) Verifier(object? json, HttpStatusCode status = HttpStatusCode.OK, Exception? fail = null)
    {
        var stub = new Stub(_ => fail is not null
            ? Task.FromException<HttpResponseMessage>(fail)
            : Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(json) }));
        var options = Options.Create(new CaptchaOptions { Provider = "Turnstile", SiteKey = "site", SecretKey = "secret" });
        return (new TurnstileVerifier(new HttpClient(stub), options, NullLogger<TurnstileVerifier>.Instance), stub);
    }

    [Fact]
    public async Task A_pass_for_this_form_passes_and_only_the_token_and_address_are_sent()
    {
        var (v, stub) = Verifier(new { success = true, action = "register" });
        Assert.Equal(CaptchaOutcome.Passed, await v.VerifyAsync("tok", "register", "203.0.113.7", default));
        Assert.Equal(new Dictionary<string, string> { ["secret"] = "secret", ["response"] = "tok", ["remoteip"] = "203.0.113.7" }, stub.Forms.Single());
    }

    [Fact]
    public async Task A_fail_a_reused_token_or_a_token_for_another_form_fails()
    {
        Assert.Equal(CaptchaOutcome.Failed, await Verifier(new { success = false, error_codes = Array.Empty<string>() }).Item1.VerifyAsync("t", "register", null, default));
        var reused = JsonSerializer.Deserialize<JsonElement>("""{"success":false,"error-codes":["timeout-or-duplicate"]}""");
        Assert.Equal(CaptchaOutcome.Failed, await Verifier(reused).Item1.VerifyAsync("t", "register", null, default));
        Assert.Equal(CaptchaOutcome.Failed, await Verifier(new { success = true, action = "login" }).Item1.VerifyAsync("t", "register", null, default));
    }

    [Fact]
    public async Task No_token_fails_without_asking()
    {
        var (v, stub) = Verifier(new { success = true });
        Assert.Equal(CaptchaOutcome.Failed, await v.VerifyAsync(null, "register", null, default));
        Assert.Equal(CaptchaOutcome.Failed, await v.VerifyAsync("  ", "register", null, default));
        Assert.Equal(CaptchaOutcome.Failed, await v.VerifyAsync(new string('x', 2049), "register", null, default));
        Assert.Empty(stub.Forms);
    }

    [Fact]
    public async Task Our_own_bad_key_or_an_outage_is_unavailable_not_a_failed_person()
    {
        var badKey = JsonSerializer.Deserialize<JsonElement>("""{"success":false,"error-codes":["invalid-input-secret"]}""");
        Assert.Equal(CaptchaOutcome.Unavailable, await Verifier(badKey).Item1.VerifyAsync("t", "register", null, default));
        Assert.Equal(CaptchaOutcome.Unavailable, await Verifier(new { }, HttpStatusCode.BadGateway).Item1.VerifyAsync("t", "register", null, default));
        Assert.Equal(CaptchaOutcome.Unavailable, await Verifier(null, fail: new HttpRequestException("down")).Item1.VerifyAsync("t", "register", null, default));
        Assert.Equal(CaptchaOutcome.Unavailable, await Verifier(null, fail: new TaskCanceledException("slow")).Item1.VerifyAsync("t", "register", null, default));
    }
}

/// <summary>Registration with the robot check: off by default, then enforced, with the outage rule as configured.</summary>
public sealed class CaptchaRegistrationTests
{
    private sealed class Fake : ICaptchaVerifier
    {
        public CaptchaOutcome Outcome { get; set; } = CaptchaOutcome.Passed;
        public List<(string? Token, string Action)> Calls { get; } = [];

        public Task<CaptchaOutcome> VerifyAsync(string? token, string action, string? ip, CancellationToken ct)
        {
            Calls.Add((token, action));
            return Task.FromResult(token is null ? CaptchaOutcome.Failed : Outcome);
        }
    }

    private static MeydanFactory Factory(Fake fake, bool on = true, bool failOpen = false)
    {
        var f = new MeydanFactory
        {
            Settings = { ["Email:OutboxWorkerEnabled"] = "false" },
            Overrides = s =>
            {
                s.RemoveAll<ICaptchaVerifier>();
                s.AddSingleton<ICaptchaVerifier>(fake);
            },
        };
        if (on)
        {
            f.Settings["Captcha:Provider"] = "Turnstile";
            f.Settings["Captcha:SiteKey"] = "0x4AAAAAAAtestsitekey";
            f.Settings["Captcha:SecretKey"] = "test-secret";
            f.Settings["Captcha:FailOpen"] = failOpen ? "true" : "false";
        }
        return f;
    }

    private static object Register(string? captchaToken) => new
    {
        firstName = "Aysel", lastName = "Məmmədova", phone = TestPhones.Next(), email = $"p-{Guid.NewGuid():N}@example.com",
        password = "secret123", confirmPassword = "secret123", acceptTerms = true, captchaToken,
    };

    [Fact]
    public async Task Off_by_default_nothing_is_asked_and_the_page_is_told_so()
    {
        var fake = new Fake();
        using var f = Factory(fake, on: false);
        var client = f.CreateClient();
        Assert.True((await client.PostAsJsonAsync("/api/auth/register", Register(null))).IsSuccessStatusCode);
        Assert.Empty(fake.Calls);
        var config = await client.GetFromJsonAsync<JsonElement>("/api/config");
        Assert.Equal(JsonValueKind.Null, config.GetProperty("captchaSiteKey").ValueKind);
    }

    [Fact]
    public async Task On_registration_needs_a_passing_token_for_the_register_form()
    {
        var fake = new Fake();
        using var f = Factory(fake);
        var client = f.CreateClient();
        Assert.Equal("0x4AAAAAAAtestsitekey", (await client.GetFromJsonAsync<JsonElement>("/api/config")).GetProperty("captchaSiteKey").GetString());

        var missing = await client.PostAsJsonAsync("/api/auth/register", Register(null));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var body = await missing.Content.ReadAsStringAsync();
        Assert.Contains("\"captcha\"", body);
        Assert.Contains("Robot olmadığınızı təsdiqləyin", body);

        fake.Outcome = CaptchaOutcome.Failed;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", Register("bad"))).StatusCode);
        Assert.Equal(0, f.WithDb(db => db.Users.Count(u => u.FirstName == "Aysel")));

        fake.Outcome = CaptchaOutcome.Passed;
        Assert.True((await client.PostAsJsonAsync("/api/auth/register", Register("good"))).IsSuccessStatusCode);
        Assert.All(fake.Calls, c => Assert.Equal("register", c.Action));
        Assert.Equal(1, f.WithDb(db => db.Users.Count(u => u.FirstName == "Aysel")));
    }

    [Fact]
    public async Task An_outage_refuses_registration_unless_the_owner_chose_to_fail_open()
    {
        var fake = new Fake { Outcome = CaptchaOutcome.Unavailable };
        using (var closed = Factory(fake))
        {
            var res = await closed.CreateClient().PostAsJsonAsync("/api/auth/register", Register("t"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            Assert.Contains("Yoxlama xidməti hazırda cavab vermir", await res.Content.ReadAsStringAsync());
        }
        using var open = Factory(fake, failOpen: true);
        Assert.True((await open.CreateClient().PostAsJsonAsync("/api/auth/register", Register("t"))).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Signing_in_is_never_asked()
    {
        var fake = new Fake();
        using var f = Factory(fake);
        var client = f.CreateClient();
        var reg = Register("good");
        Assert.True((await client.PostAsJsonAsync("/api/auth/register", reg)).IsSuccessStatusCode);
        var email = (string)reg.GetType().GetProperty("email")!.GetValue(reg)!;
        fake.Calls.Clear();
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "secret123" })).StatusCode);
        Assert.Empty(fake.Calls);
    }
}
