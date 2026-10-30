using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Meydan.Api.Tests;

/// <summary>
/// Phase 5 final review: e-mail goes through a durable PostgreSQL outbox. Pending mail survives a restart, rows hold
/// references only (no address, body or token), two workers never send the same row concurrently, retries are
/// bounded with a growing delay, and a stale reset request expires instead of being sent late.
/// </summary>
public sealed partial class EmailOutboxTests
{
    [GeneratedRegex(@"/reset-password#email=[^&\s]+&token=(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex ResetLink();

    private const string Chevron = "baki-kendleri-sevron-dizayni";

    private static MeydanFactory Factory(CapturingEmailSender mail, Dictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?> { ["App:PublicUrl"] = "https://meydan.test" };
        foreach (var (k, v) in settings ?? []) all[k] = v;
        return new MeydanFactory { Mail = mail, Settings = all };
    }

    /// <summary>A separate host ("process") on the same database; disposing it is a full stop.</summary>
    private static WebApplicationFactory<Program> Host(MeydanFactory factory, bool workerEnabled) =>
        factory.WithWebHostBuilder(b => b.UseSetting("Email:OutboxWorkerEnabled", workerEnabled ? "true" : "false"));

    private static EmailOutboxWorker Worker(WebApplicationFactory<Program> host) =>
        host.Services.GetServices<IHostedService>().OfType<EmailOutboxWorker>().Single();

    private static List<EmailOutboxMessage> Rows(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<Meydan.Infrastructure.Persistence.MeydanDbContext>()
            .EmailOutbox.AsNoTracking().OrderBy(m => m.Id).ToList();
    }

    private static async Task<List<EmailOutboxMessage>> WaitForRowsAsync(WebApplicationFactory<Program> host, Func<List<EmailOutboxMessage>, bool> done)
    {
        var rows = Rows(host);
        for (var i = 0; i < 200 && !done(rows); i++)
        {
            await Task.Delay(50);
            rows = Rows(host);
        }
        return rows;
    }

    /// <summary>Raw text of every outbox row, exactly as stored.</summary>
    private static string RawOutbox(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Meydan.Infrastructure.Persistence.MeydanDbContext>();
        return string.Join("\n", db.Database.SqlQuery<string>($"""SELECT row_to_json(e)::text AS "Value" FROM "EmailOutbox" e""").ToList());
    }

    /// <summary>Registers an account and queues <paramref name="count"/> reset mails for it directly.</summary>
    private static async Task EnqueueResetsAsync(WebApplicationFactory<Program> host, string email, int count)
    {
        await host.CreateClient().RegisterAsync(email);
        using var scope = host.Services.CreateScope();
        var userId = scope.ServiceProvider.GetRequiredService<Meydan.Infrastructure.Persistence.MeydanDbContext>()
            .Users.Single(u => u.Email == email).Id;
        var outbox = scope.ServiceProvider.GetRequiredService<IEmailOutbox>();
        for (var i = 0; i < count; i++)
            await outbox.EnqueueAsync("password-reset", userId, null, null, CancellationToken.None);
    }

    private static string RefreshCookie(HttpResponseMessage res) =>
        res.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => !c.EndsWith('='));

    private static async Task<HttpResponseMessage> RefreshWithCookie(HttpClient client, string cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        req.Headers.Add("Cookie", cookie);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task A_reset_mail_queued_before_a_restart_is_sent_after_it_and_completes_the_flow()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail);
        const string email = "restart@example.com";
        string oldCookie;

        // Process 1: the request is accepted and queued, then the process stops before any delivery.
        using (var first = Host(factory, workerEnabled: false))
        {
            var laptop = first.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var register = await laptop.PostAsJsonAsync("/api/auth/register", new
            {
                firstName = "Aysel", lastName = "Məmmədova", phone = TestPhones.Next(),
                email, password = "oldpass123", confirmPassword = "oldpass123", acceptTerms = true,
            });
            register.EnsureSuccessStatusCode();
            oldCookie = RefreshCookie(register);

            Assert.Equal(HttpStatusCode.NoContent,
                (await laptop.PostAsJsonAsync("/api/auth/forgot-password", new { email })).StatusCode);
            await Task.Delay(300);
            Assert.Empty(mail.Sent);

            var row = Assert.Single(Rows(first));
            Assert.Equal(EmailOutboxStatus.Pending, row.Status);
            Assert.Equal("password-reset", row.Kind);
            Assert.Equal(0, row.AttemptCount);
            // Nothing sensitive at rest: no address, no link, no token, no body.
            var raw = RawOutbox(first);
            Assert.DoesNotContain(email, raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reset-password", raw);
            Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Aysel", raw);
        }

        // Process 2: the pending row is picked up and delivered.
        using var second = Host(factory, workerEnabled: true);
        var client = second.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var message = await mail.WaitForAsync("password-reset", email);
        var token = ResetLink().Match(message.TextBody).Groups["token"].Value;
        Assert.NotEmpty(token);
        var sent = Assert.Single(await WaitForRowsAsync(second, r => r.All(x => x.Status == EmailOutboxStatus.Sent)));
        Assert.Equal(EmailOutboxStatus.Sent, sent.Status);
        Assert.Equal(1, sent.AttemptCount);
        Assert.Null(sent.LockedBy);

        // The session from before the restart still works until the reset...
        var rotated = await RefreshWithCookie(client, oldCookie);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var liveCookie = RefreshCookie(rotated);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/reset-password",
            new { email, token, password = "newpass123", confirmPassword = "newpass123" })).StatusCode);

        // ...and not after it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshWithCookie(client, liveCookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "oldpass123" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "newpass123" })).StatusCode);
    }

    [Fact]
    public async Task A_submission_receipt_queued_before_a_restart_is_sent_after_it()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail);
        const string email = "receipt-restart@example.com";
        int id;
        string reference;

        using (var first = Host(factory, workerEnabled: false))
        {
            var client = first.CreateClient();
            client.UseToken(await client.RegisterAsync(email));
            var draft = await (await client.PostAsJsonAsync("/api/submissions",
                new { competitionSlug = Chevron, categorySlug = "mastaga" })).Content.ReadFromJsonAsync<JsonElement>();
            id = draft.GetProperty("id").GetInt32();
            (await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title = "İş", concept = "K", symbols = "S" } }))
                .EnsureSuccessStatusCode();
            var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
            var file = new ByteArrayContent(TestImages.Png());
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "a.png");
            (await client.PostAsync($"/api/submissions/{id}/files", form)).EnsureSuccessStatusCode();
            var submitted = await client.PostAsync($"/api/submissions/{id}/submit", null);
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            reference = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("referenceNumber").GetString()!;

            var row = Assert.Single(Rows(first));
            Assert.Equal(("submission-received", id, EmailOutboxStatus.Pending), (row.Kind, row.SubjectId!.Value, row.Status));
            Assert.DoesNotContain(reference, RawOutbox(first));
        }
        Assert.Empty(mail.Sent);

        using var second = Host(factory, workerEnabled: true);
        _ = second.Services; // start the host
        var message = await mail.WaitForAsync("submission-received", email);
        Assert.Contains(reference, message.Subject);
        Assert.Contains($"https://meydan.test/submissions/{id}", message.TextBody);
        Assert.Equal(EmailOutboxStatus.Sent,
            Assert.Single(await WaitForRowsAsync(second, r => r.All(x => x.Status == EmailOutboxStatus.Sent))).Status);
    }

    [Fact]
    public async Task Two_workers_racing_over_the_same_rows_send_each_message_exactly_once()
    {
        var mail = new CapturingEmailSender { Delay = TimeSpan.FromMilliseconds(15) };
        using var factory = Factory(mail, new() { ["Email:BatchSize"] = "3" });
        using var a = Host(factory, workerEnabled: false);
        using var b = Host(factory, workerEnabled: false);

        const int total = 40;
        await EnqueueResetsAsync(a, "race@example.com", total);

        var workers = new[] { Worker(a), Worker(b) };
        while (true)
        {
            // Eight concurrent claim/deliver passes, alternating between the two processes.
            var claimed = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => workers[i % 2].ProcessBatchAsync(CancellationToken.None))));
            if (claimed.Sum() == 0) break;
        }

        Assert.Equal(total, mail.Sent.Count);
        Assert.Equal(total, mail.Attempts);
        var rows = Rows(a);
        Assert.Equal(total, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(EmailOutboxStatus.Sent, r.Status);
            Assert.Equal(1, r.AttemptCount);
        });
    }

    [Fact]
    public async Task Both_background_workers_running_still_deliver_each_row_once()
    {
        var mail = new CapturingEmailSender { Delay = TimeSpan.FromMilliseconds(10) };
        using var factory = Factory(mail, new() { ["Email:BatchSize"] = "2", ["Email:PollSeconds"] = "1" });
        using var a = Host(factory, workerEnabled: false);
        await EnqueueResetsAsync(a, "bg-race@example.com", 30);

        using var p1 = Host(factory, workerEnabled: true);
        using var p2 = Host(factory, workerEnabled: true);
        _ = p1.Services;
        _ = p2.Services;
        var rows = await WaitForRowsAsync(a, r => r.All(x => x.Status == EmailOutboxStatus.Sent));

        Assert.All(rows, r => Assert.Equal((EmailOutboxStatus.Sent, 1), (r.Status, r.AttemptCount)));
        await Task.Delay(300);
        Assert.Equal(30, mail.Sent.Count);
    }

    [Fact]
    public async Task Retries_wait_longer_each_time_and_stop_at_the_limit()
    {
        var mail = new CapturingEmailSender { Fail = true };
        using var factory = Factory(mail, new() { ["Email:MaxAttempts"] = "3", ["Email:RetryDelaySeconds"] = "60" });
        using var host = Host(factory, workerEnabled: false);
        var client = host.CreateClient();
        await client.RegisterAsync("retry@example.com");
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "retry@example.com" });
        var worker = Worker(host);
        var t0 = factory.Clock.Now;

        Assert.Equal(1, await worker.ProcessBatchAsync(CancellationToken.None));
        var row = Assert.Single(Rows(host));
        Assert.Equal((EmailOutboxStatus.Pending, 1, "SocketException"), (row.Status, row.AttemptCount, row.LastError));
        Assert.InRange((row.NextAttemptAt - t0).TotalSeconds, 59.99, 60.01);
        Assert.Null(row.LockedBy);

        Assert.Equal(0, await worker.ProcessBatchAsync(CancellationToken.None)); // not due yet

        factory.Clock.Now = t0.AddSeconds(61);
        Assert.Equal(1, await worker.ProcessBatchAsync(CancellationToken.None));
        row = Assert.Single(Rows(host));
        Assert.Equal(2, row.AttemptCount);
        Assert.InRange((row.NextAttemptAt - factory.Clock.Now).TotalSeconds, 179.99, 180.01); // 60 s, then 3 × 60 s

        factory.Clock.Now = factory.Clock.Now.AddSeconds(181);
        Assert.Equal(1, await worker.ProcessBatchAsync(CancellationToken.None));
        row = Assert.Single(Rows(host));
        Assert.Equal((EmailOutboxStatus.Failed, 3), (row.Status, row.AttemptCount));

        factory.Clock.Now = factory.Clock.Now.AddHours(2);
        Assert.Equal(0, await worker.ProcessBatchAsync(CancellationToken.None)); // a failed row is never retried
        Assert.Equal(3, mail.Attempts);
        var lines = factory.Logs.Lines.ToArray();
        Assert.Contains(lines, l => l.StartsWith("Error") && l.Contains("given up after 3 attempts") && l.Contains("r***@example.com"));
        Assert.DoesNotContain(lines, l => l.Contains("retry@example.com") || l.Contains("token="));
    }

    [Fact]
    public async Task A_reset_mail_older_than_the_token_lifetime_expires_instead_of_being_sent()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail);
        using var host = Host(factory, workerEnabled: false);
        var client = host.CreateClient();
        await client.RegisterAsync("stale@example.com");
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "stale@example.com" });
        var row = Assert.Single(Rows(host));
        Assert.InRange((row.ExpiresAt!.Value - row.CreatedAt).TotalMinutes, 59.99, 60.01); // Auth:PasswordResetTokenLifetime

        factory.Clock.Now = factory.Clock.Now.AddMinutes(61);
        Assert.Equal(1, await Worker(host).ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(EmailOutboxStatus.Expired, Assert.Single(Rows(host)).Status);
        Assert.Equal(0, mail.Attempts);
    }

    [Fact]
    public async Task A_row_left_locked_by_a_crashed_worker_is_taken_over_once_its_lease_runs_out()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail, new() { ["Email:LeaseSeconds"] = "120" });
        using var host = Host(factory, workerEnabled: false);
        var client = host.CreateClient();
        await client.RegisterAsync("lease@example.com");
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "lease@example.com" });
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Meydan.Infrastructure.Persistence.MeydanDbContext>();
            var row = db.EmailOutbox.Single();
            // What a process that died mid-send leaves behind.
            row.LockedBy = "dead-host:1:x";
            row.LockedUntil = factory.Clock.Now.AddSeconds(120);
            row.AttemptCount = 1;
            db.SaveChanges();
        }
        var worker = Worker(host);

        Assert.Equal(0, await worker.ProcessBatchAsync(CancellationToken.None));
        factory.Clock.Now = factory.Clock.Now.AddSeconds(121);
        Assert.Equal(1, await worker.ProcessBatchAsync(CancellationToken.None));
        var done = Assert.Single(Rows(host));
        Assert.Equal((EmailOutboxStatus.Sent, 2), (done.Status, done.AttemptCount));
        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task A_worker_whose_batch_lease_ran_out_during_a_slow_send_leaves_the_rest_to_others()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail, new() { ["Email:BatchSize"] = "3", ["Email:LeaseSeconds"] = "30" });
        using var a = Host(factory, workerEnabled: false);
        using var b = Host(factory, workerEnabled: false);
        await EnqueueResetsAsync(a, "slow@example.com", 3);

        // The first send takes longer than the whole batch's lease.
        mail.DuringSend = () => { factory.Clock.Now = factory.Clock.Now.AddSeconds(31); mail.DuringSend = null; };
        Assert.Equal(3, await Worker(a).ProcessBatchAsync(CancellationToken.None));
        Assert.Single(mail.Sent); // rows 2 and 3 were no longer A's to send

        Assert.Equal(2, await Worker(b).ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(3, mail.Sent.Count);
        Assert.All(Rows(a), r => Assert.Equal(EmailOutboxStatus.Sent, r.Status));
    }

    [Fact]
    public async Task If_queuing_fails_the_submission_still_stands_and_the_reset_answer_is_unchanged()
    {
        var mail = new CapturingEmailSender();
        using var factory = Factory(mail);
        var client = factory.CreateClient();
        client.UseToken(await client.RegisterAsync("noqueue@example.com"));
        var draft = await (await client.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = Chevron, categorySlug = "mastaga" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = draft.GetProperty("id").GetInt32();
        (await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title = "İş", concept = "K", symbols = "S" } }))
            .EnsureSuccessStatusCode();
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "a.png");
        (await client.PostAsync($"/api/submissions/{id}/files", form)).EnsureSuccessStatusCode();

        // The outbox table becomes unusable (e.g. a broken deployment).
        await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("""ALTER TABLE "EmailOutbox" RENAME TO "EmailOutboxGone" """));

        var submitted = await client.PostAsync($"/api/submissions/{id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        Assert.Equal("Submitted", factory.WithDb(db => db.Submissions.Single(s => s.Id == id).Status.ToString()));
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "noqueue@example.com" })).StatusCode);

        var lines = factory.Logs.Lines.ToArray();
        Assert.Contains(lines, l => l.Contains("Receipt e-mail for") && l.Contains("not queued"));
        Assert.Contains(lines, l => l.Contains("Password reset e-mail for user") && l.Contains("not queued"));
    }
}
