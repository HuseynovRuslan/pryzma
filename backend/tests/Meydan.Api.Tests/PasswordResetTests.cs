using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meydan.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>Phase 5: password recovery by e-mail — no account enumeration, short-lived single-use tokens,
/// sessions ended after a reset, throttling, and an SMTP outage that never costs anyone their work.</summary>
public sealed partial class PasswordResetTests
{
    [GeneratedRegex(@"(?<url>https?://[^/\s]+)/reset-password#email=(?<email>[^&\s]+)&token=(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex Link();

    private static MeydanFactory Factory(CapturingEmailSender mail, Dictionary<string, string?>? settings = null) =>
        new() { Mail = mail, Settings = settings ?? new() { ["App:PublicUrl"] = "https://meydan.test" } };

    private static Task<HttpResponseMessage> Forgot(HttpClient c, string email) =>
        c.PostAsJsonAsync("/api/auth/forgot-password", new { email });

    private static Task<HttpResponseMessage> Reset(HttpClient c, string email, string token, string password) =>
        c.PostAsJsonAsync("/api/auth/reset-password", new { email, token, password, confirmPassword = password });

    [Fact]
    public async Task The_answer_is_the_same_whether_or_not_the_account_exists()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail);
        var client = factory.CreateClient();
        await client.RegisterAsync("real@example.com");

        var known = await Forgot(client, "real@example.com");
        var unknown = await Forgot(client, "nobody@example.com");
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, known.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());

        await mail.WaitForAsync("password-reset", "real@example.com");
        await Task.Delay(300);
        Assert.DoesNotContain(mail.Sent, m => m.To == "nobody@example.com");
    }

    [Fact]
    public async Task The_mail_links_to_the_configured_https_site_whatever_host_the_request_used()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail);
        var client = factory.CreateClient();
        await client.RegisterAsync("a+b@example.com");
        // The mail is signed with the platform's name as the admin set it.
        await factory.WithDbAsync(async db =>
        {
            var settings = await db.PlatformSettings.FirstOrDefaultAsync();
            if (settings is null) db.PlatformSettings.Add(new Meydan.Domain.Entities.PlatformSettings { PlatformName = "PRYZMA" });
            else settings.PlatformName = "PRYZMA";
            await db.SaveChangesAsync();
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/forgot-password") { Content = JsonContent.Create(new { email = "a+b@example.com" }) };
        request.Headers.Host = "evil.example";
        request.Headers.Add("X-Forwarded-Host", "evil.example");
        await client.SendAsync(request);

        var message = await mail.WaitForAsync("password-reset", "a+b@example.com");
        var match = Link().Match(message.TextBody);
        Assert.True(match.Success);
        Assert.Equal("https://meydan.test", match.Groups["url"].Value);
        Assert.Equal("a%2Bb%40example.com", match.Groups["email"].Value);
        Assert.DoesNotContain("evil.example", message.HtmlBody + message.TextBody);
        Assert.Contains("Yeni şifrə təyin et", message.HtmlBody);
        Assert.Contains("60 dəqiqə", message.TextBody);
        Assert.Equal("PRYZMA: şifrənin yenilənməsi", message.Subject);
        Assert.Contains("PRYZMA hesabınız", message.TextBody);
        Assert.Contains("PRYZMA —", message.HtmlBody);
        Assert.DoesNotContain("MEYDAN", message.HtmlBody + message.TextBody);
    }

    [Fact]
    public async Task Tokens_are_single_use_and_bad_tokens_are_refused()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail);
        var client = factory.CreateClient();
        await client.RegisterAsync("once@example.com", "oldpass123");
        await Forgot(client, "once@example.com");
        var token = Link().Match((await mail.WaitForAsync("password-reset", "once@example.com")).TextBody).Groups["token"].Value;

        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(client, "once@example.com", "bm90LWEtdG9rZW4", "newpass123")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(client, "once@example.com", "%%%", "newpass123")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(client, "someone-else@example.com", token, "newpass123")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Reset(client, "once@example.com", token, "newpass123")).StatusCode);
        var reuse = await Reset(client, "once@example.com", token, "another123");
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Contains("etibarsızdır", await reuse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_reset_lets_a_locked_out_person_in_with_the_new_password_at_once()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail);
        var client = factory.CreateClient();
        await client.RegisterAsync("locked@example.com", "oldpass123");
        Task<HttpResponseMessage> Login(string password) =>
            factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "locked@example.com", password });

        // The old password is forgotten: five wrong tries lock the account for ten minutes.
        for (var i = 0; i < 5; i++) await Login("wrong-guess");
        Assert.Equal((HttpStatusCode)423, (await Login("oldpass123")).StatusCode);

        await Forgot(client, "locked@example.com");
        var token = Link().Match((await mail.WaitForAsync("password-reset", "locked@example.com")).TextBody).Groups["token"].Value;
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(client, "locked@example.com", token, "newpass123")).StatusCode);

        // The new password works straight away, and one slip afterwards does not lock the account again.
        Assert.Equal(HttpStatusCode.OK, (await Login("newpass123")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login("newpass12")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login("newpass123")).StatusCode);
    }

    [Fact]
    public async Task Tokens_expire()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail, new() { ["Auth:PasswordResetTokenLifetime"] = "00:00:02" });
        var client = factory.CreateClient();
        await client.RegisterAsync("late@example.com", "oldpass123");
        await Forgot(client, "late@example.com");
        var token = Link().Match((await mail.WaitForAsync("password-reset", "late@example.com")).TextBody).Groups["token"].Value;

        await Task.Delay(TimeSpan.FromSeconds(3)); // Identity's token provider uses the system clock
        var res = await Reset(client, "late@example.com", token, "newpass123");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("vaxtı bitib", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_reset_ends_every_existing_session_including_access_tokens_already_issued()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail);
        var laptop = factory.CreateClient();           // keeps its refresh cookie
        laptop.UseToken(await laptop.RegisterAsync("sessions@example.com", "oldpass123"));
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/me/profile")).StatusCode);
        // Ordinary account changes do not end the session.
        Assert.Equal(HttpStatusCode.OK, (await laptop.PutAsJsonAsync("/api/me/profile",
            new { firstName = "Aysel", lastName = "Əliyeva", phone = TestPhones.Next() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/me/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.PostAsync("/api/auth/refresh", null)).StatusCode);

        var phone = factory.CreateClient();
        await Forgot(phone, "sessions@example.com");
        var token = Link().Match((await mail.WaitForAsync("password-reset", "sessions@example.com")).TextBody).Groups["token"].Value;
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(phone, "sessions@example.com", token, "newpass123")).StatusCode);

        // The laptop's access token is still well inside its 15 minutes (the test clock has not moved), yet it is
        // refused at once: the reset rotated the security stamp it was issued with. Its refresh token is revoked.
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/me/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.PostAsync("/api/auth/refresh", null)).StatusCode);

        // Signing in again with the new password gives a working session.
        var fresh = factory.CreateClient();
        fresh.UseToken(await fresh.LoginAsync("sessions@example.com", "newpass123"));
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/me/profile")).StatusCode);
    }

    [Fact]
    public async Task Password_endpoints_are_throttled_with_an_azerbaijani_message()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail, new() { ["RateLimiting:PasswordPermitsPer15Minutes"] = "3" });
        var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await Forgot(client, $"x{i}@example.com")).StatusCode);

        var limited = await Forgot(client, "x9@example.com");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null);
        var body = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Çox sayda sorğu göndərildi.", body.GetProperty("title").GetString());
        // Login still works: throttling is per endpoint group.
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync("/api/auth/login", new { email = "x@example.com", password = "whatever1" })).StatusCode);
    }

    [Fact]
    public async Task When_the_mail_server_is_down_requests_still_succeed_and_nothing_secret_is_logged()
    {
        var mail = new CapturingEmailSender { Fail = true };
        using var factory = Factory(mail, new() { ["Email:MaxAttempts"] = "2" });
        var client = factory.CreateClient();
        client.UseToken(await client.RegisterAsync("down@example.com", "oldpass123"));

        Assert.Equal(HttpStatusCode.NoContent, (await Forgot(client, "down@example.com")).StatusCode);

        // A submission is committed even though its receipt cannot be delivered.
        var draft = await (await client.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = "baki-kendleri-sevron-dizayni", categorySlug = "mastaga" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = draft.GetProperty("id").GetInt32();
        await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title = "İş", concept = "K", symbols = "S" } });
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new("image/png");
        form.Add(file, "file", "a.png");
        await client.PostAsync($"/api/submissions/{id}/files", form);
        var submitted = await client.PostAsync($"/api/submissions/{id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        Assert.Equal("Submitted", factory.WithDb(db => db.Submissions.Single(s => s.Id == id).Status.ToString()));

        string[] lines = [];
        for (var i = 0; i < 100 && !lines.Any(l => l.Contains("given up") && l.Contains("submission-received")); i++)
        {
            await Task.Delay(50);
            lines = factory.Logs.Lines.ToArray();
        }
        Assert.Contains(lines, l => l.Contains("password-reset") && l.Contains("given up") && l.Contains("d***@example.com"));
        Assert.Contains(lines, l => l.Contains("submission-received") && l.Contains("given up"));
        Assert.DoesNotContain(lines, l => l.Contains("down@example.com"));
        Assert.DoesNotContain(lines, l => l.Contains("token=") || l.Contains("reset-password#"));
        Assert.True(mail.Attempts >= 4);
    }

    [Fact]
    public async Task A_submission_sends_a_receipt_with_its_reference()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail);
        var client = factory.CreateClient();
        client.UseToken(await client.RegisterAsync("receipt@example.com"));
        var draft = await (await client.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = "baki-kendleri-sevron-dizayni", categorySlug = "mastaga" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = draft.GetProperty("id").GetInt32();
        await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title = "İş", concept = "K", symbols = "S" } });
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new("image/png");
        form.Add(file, "file", "a.png");
        await client.PostAsync($"/api/submissions/{id}/files", form);
        var reference = (await (await client.PostAsync($"/api/submissions/{id}/submit", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("referenceNumber").GetString()!;

        var message = await mail.WaitForAsync("submission-received", "receipt@example.com");
        Assert.Equal($"Təklifiniz qəbul edildi — {reference}", message.Subject);
        Assert.Contains(reference, message.TextBody);
        Assert.Contains("Maştağa", message.HtmlBody);
        Assert.Contains($"https://meydan.test/submissions/{id}", message.TextBody);
        Assert.DoesNotContain("Konsept", message.TextBody); // no submitted content in the mail
    }
}
