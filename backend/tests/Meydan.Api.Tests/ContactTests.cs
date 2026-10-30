using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Features.Auth;
using Meydan.Api.Features.Contact;
using Meydan.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meydan.Api.Tests;

/// <summary>
/// The site's contact form: one e-mail to the organiser (appsettings Contact:ToAddress) that replies to the person
/// who wrote, nothing stored, failures said rather than swallowed, behind the robot check and a per-address limit.
/// </summary>
public sealed class ContactTests
{
    private const string Organiser = "huseynovruslaninfo@gmail.com";

    private sealed class FakeCaptcha : ICaptchaVerifier
    {
        public List<(string? Token, string Action)> Calls { get; } = [];

        public Task<CaptchaOutcome> VerifyAsync(string? token, string action, string? ip, CancellationToken ct)
        {
            Calls.Add((token, action));
            return Task.FromResult(token is null ? CaptchaOutcome.Failed : CaptchaOutcome.Passed);
        }
    }

    private static MeydanFactory Factory(CapturingEmailSender? mail, FakeCaptcha? captcha = null)
    {
        var f = new MeydanFactory
        {
            Mail = mail,
            Settings = { ["Email:OutboxWorkerEnabled"] = "false" },
            Overrides = captcha is null
                ? null
                : s =>
                {
                    s.RemoveAll<ICaptchaVerifier>();
                    s.AddSingleton<ICaptchaVerifier>(captcha);
                },
        };
        if (captcha is not null)
        {
            f.Settings["Captcha:Provider"] = "Turnstile";
            f.Settings["Captcha:SiteKey"] = "0x4AAAAAAAtestsitekey";
            f.Settings["Captcha:SecretKey"] = "test-secret";
        }
        return f;
    }

    private static object Message(
        string name = "Aysel Məmmədova", string email = "aysel@example.com",
        string message = "Salam! Növbəti müsabiqə haqqında sualım var.", string? captchaToken = null) =>
        new { name, email, message, captchaToken };

    [Fact]
    public async Task Sends_one_mail_to_the_organiser_that_replies_to_the_person_who_wrote()
    {
        var mail = new CapturingEmailSender();
        using var f = Factory(mail);
        var client = f.CreateClient();

        var res = await client.PostAsJsonAsync("/api/contact", Message(
            name: "  Aysel \r\n Məmmədova ", message: "<b>Salam</b>, bu bir sınaqdır.\r\nİkinci sətir."));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var sent = Assert.Single(mail.Sent);
        Assert.Equal(Organiser, sent.To);
        Assert.Equal("contact-message", sent.Kind);
        Assert.Equal("aysel@example.com", sent.ReplyTo);
        // One line wherever the name goes: no header can be broken by it.
        Assert.Equal("Aysel Məmmədova", sent.ReplyToName);
        Assert.Contains("Aysel Məmmədova", sent.Subject);
        Assert.DoesNotContain('\n', sent.Subject);
        // The words as written, never as markup.
        Assert.Contains("&lt;b&gt;Salam&lt;/b&gt;", sent.HtmlBody);
        Assert.DoesNotContain("<b>Salam</b>", sent.HtmlBody);
        Assert.Contains("<b>Salam</b>, bu bir sınaqdır.\nİkinci sətir.", sent.TextBody);
        // Nothing of it is kept: not queued, not stored.
        Assert.Equal(0, f.WithDb(db => db.EmailOutbox.Count()));
    }

    [Fact]
    public async Task Refuses_an_incomplete_form_field_by_field_and_sends_nothing()
    {
        var mail = new CapturingEmailSender();
        using var f = Factory(mail);
        var res = await f.CreateClient().PostAsJsonAsync("/api/contact", new { name = " ", email = "ünvan-deyil", message = "qısa" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var errors = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("Adınızı yazın.", errors.GetProperty("name")[0].GetString());
        Assert.Equal("E-poçt ünvanı düzgün deyil.", errors.GetProperty("email")[0].GetString());
        Assert.Equal("Mesaj 10–4000 simvol olmalıdır.", errors.GetProperty("message")[0].GetString());
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Says_so_when_the_mail_cannot_go_rather_than_pretending()
    {
        // Mail switched off (no Email:Mode here): the form is refused, not silently dropped.
        using (var off = Factory(mail: null))
        {
            var res = await off.CreateClient().PostAsJsonAsync("/api/contact", Message());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            Assert.Equal(ContactMessages.Unavailable, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        }

        // The mail server refuses: the person is told to try again.
        var mail = new CapturingEmailSender { Fail = true };
        using var f = Factory(mail);
        var failed = await f.CreateClient().PostAsJsonAsync("/api/contact", Message());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(ContactMessages.NotSent, (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Asks_for_the_robot_check_of_its_own_form_when_it_is_on()
    {
        var mail = new CapturingEmailSender();
        var captcha = new FakeCaptcha();
        using var f = Factory(mail, captcha);
        var client = f.CreateClient();

        var refused = await client.PostAsJsonAsync("/api/contact", Message());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(CaptchaGate.Failed,
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("captcha")[0].GetString());
        Assert.Empty(mail.Sent);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/contact", Message(captchaToken: "token-1"))).StatusCode);
        Assert.Single(mail.Sent);
        // A token made for registration is not accepted here: the check is asked for this form's action.
        Assert.All(captcha.Calls, c => Assert.Equal("contact", c.Action));
    }

    [Fact]
    public async Task Limits_one_address_to_a_few_messages_an_hour()
    {
        var mail = new CapturingEmailSender();
        using var f = Factory(mail);
        f.Settings["RateLimiting:ContactMessagesPerHour"] = "2";
        var client = f.CreateClient();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            codes.Add((await client.PostAsJsonAsync("/api/contact", Message())).StatusCode);

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.TooManyRequests], codes);
        Assert.Equal(2, mail.Sent.Count);
    }

    [Fact]
    public async Task The_mail_itself_comes_from_us_and_replies_to_the_person()
    {
        // The real message as it leaves (pickup directory instead of an SMTP server): headers, not only the record.
        var dir = Path.Combine(Path.GetTempPath(), "meydan-contact-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var f = new MeydanFactory
            {
                Settings =
                {
                    ["Email:OutboxWorkerEnabled"] = "false",
                    ["Email:Mode"] = "Pickup",
                    ["Email:PickupDirectory"] = dir,
                    ["Email:FromAddress"] = "no-reply@meydan.test",
                },
            };
            var res = await f.CreateClient().PostAsJsonAsync("/api/contact", Message());
            Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

            var eml = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(dir, "*.eml")));
            Assert.Contains("From: PRYZMA <no-reply@meydan.test>", eml);
            Assert.Contains($"To: {Organiser}", eml);
            Assert.Matches(@"Reply-To: .*<aysel@example\.com>", eml);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
