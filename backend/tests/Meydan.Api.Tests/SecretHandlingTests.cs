using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Infrastructure.Email;
using Microsoft.Extensions.DependencyInjection;

namespace Meydan.Api.Tests;

public sealed partial class SecretHandlingTests
{
    [GeneratedRegex(@"/reset-password#email=[^&\s]+&token=(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex ResetLink();

    private static async Task<string[]> WaitForLogAsync(MeydanFactory factory, Func<string, bool> predicate)
    {
        for (var i = 0; i < 100; i++)
        {
            var lines = factory.Logs.Lines.ToArray();
            if (lines.Any(predicate)) return lines;
            await Task.Delay(50);
        }
        return factory.Logs.Lines.ToArray();
    }

    [Fact]
    public async Task Outside_development_reset_tokens_never_reach_the_logs()
    {
        using var factory = new MeydanFactory(); // "Testing" = any non-Development host; e-mail mode Disabled
        var client = factory.CreateClient();
        await client.RegisterAsync("logs@example.com");

        var res = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "logs@example.com" });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var lines = await WaitForLogAsync(factory, l => l.Contains("E-mail not sent"));
        Assert.DoesNotContain(lines, l => l.Contains("reset-password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("token=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("logs@example.com", StringComparison.OrdinalIgnoreCase));
        // The drop is still visible to operators, with a masked recipient.
        Assert.Contains(lines, l => l.Contains("E-mail not sent") && l.Contains("l***@example.com"));
    }

    [Theory]
    [InlineData("Production", "Disabled", typeof(DisabledEmailSender))]
    [InlineData("Production", "Smtp", typeof(SmtpEmailSender))]
    [InlineData("Staging", "Disabled", typeof(DisabledEmailSender))]
    [InlineData("Development", null, typeof(LogEmailSender))]
    [InlineData("Testing", "Pickup", typeof(PickupDirectoryEmailSender))]
    public void The_sender_follows_configuration_and_only_development_logs_bodies(string environment, string? mode, Type expected)
    {
        using var factory = new MeydanFactory
        {
            EnvironmentName = environment,
            ProductionLike = environment is "Production" or "Staging",
            Settings = new()
            {
                ["Email:Mode"] = mode,
                ["Email:Smtp:Host"] = "smtp.example.test",
                ["Email:FromAddress"] = "no-reply@example.test",
                ["Email:PickupDirectory"] = Path.Combine(Path.GetTempPath(), "meydan-pickup-" + Guid.NewGuid().ToString("N")),
            },
        };
        Assert.IsType(expected, factory.Services.GetRequiredService<IEmailSender>());
    }

    [Theory]
    [InlineData("Log")]
    [InlineData("Pickup")]
    public void Production_refuses_modes_that_write_message_contents(string mode)
    {
        using var factory = new MeydanFactory { EnvironmentName = "Production", ProductionLike = true, Settings = new() { ["Email:Mode"] = mode } };
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Email:Mode", ex.ToString());
    }

    [Fact]
    public async Task Reset_link_keeps_the_token_in_the_fragment_and_completes_the_flow()
    {
        var mail = new CapturingEmailSender();
        using var factory = new MeydanFactory { Mail = mail };
        var client = factory.CreateClient();
        await client.RegisterAsync("reset-flow@example.com", "oldpass123");

        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "reset-flow@example.com" });

        var message = await mail.WaitForAsync("password-reset", "reset-flow@example.com");
        Assert.DoesNotContain("reset-password?", message.TextBody); // no query string anywhere
        Assert.DoesNotContain("reset-password?", message.HtmlBody);
        var token = ResetLink().Match(message.TextBody).Groups["token"].Value;
        Assert.NotEmpty(token);

        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new
        {
            email = "reset-flow@example.com", token, password = "newpass123", confirmPassword = "newpass123",
        });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "reset-flow@example.com", password = "newpass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // The old password stops working.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
                new { email = "reset-flow@example.com", password = "oldpass123" })).StatusCode);
    }

    [Fact]
    public void Without_seed_credentials_no_admin_account_is_created()
    {
        using var factory = new MeydanFactory { OmitAdminSeed = true };
        factory.CreateClient();
        Assert.Equal(0, factory.WithDb(db => db.Users.Count()));
    }

    [Fact]
    public void Missing_signing_key_fails_fast_with_guidance()
    {
        using var factory = new MeydanFactory { OmitSigningKey = true };
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("user-secrets", ex.ToString());
    }
}
