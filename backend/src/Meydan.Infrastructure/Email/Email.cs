using System.Collections.Concurrent;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Meydan.Infrastructure.Email;

/// <summary>
/// One outgoing message. Bodies may contain one-time secrets (password-reset links), so they are never logged
/// outside local development and never persisted by the application.
/// </summary>
/// <param name="Kind">Short, non-sensitive label for logs, e.g. "password-reset".</param>
/// <param name="ReplyTo">Where "Reply" goes instead of the no-reply sender (the contact form: the person who wrote).</param>
/// <param name="ReplyToName">The display name shown with <paramref name="ReplyTo"/>.</param>
public sealed record EmailMessage(
    string To, string Subject, string HtmlBody, string TextBody, string Kind, string? ReplyTo = null, string? ReplyToName = null);

/// <summary>Delivers one message now. Provider-neutral: SMTP today, an HTTP API provider tomorrow.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public enum EmailMode
{
    /// <summary>Nothing is sent; a warning with a masked recipient is logged.</summary>
    Disabled,
    /// <summary>Development only: the message, including links, goes to the log.</summary>
    Log,
    /// <summary>Non-production only: each message is written as an .eml file (QA / e2e tests).</summary>
    Pickup,
    Smtp,
}

public enum SmtpSecurity
{
    /// <summary>STARTTLS required (typically port 587).</summary>
    StartTls,
    /// <summary>Implicit TLS from the first byte (typically port 465).</summary>
    SslOnConnect,
    /// <summary>No TLS — only for a relay on localhost / a private network.</summary>
    None,
}

public sealed class SmtpSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// This account's own sender. A mailbox service sends only as the account that signed in, so a second account
    /// needs its own; unset, the first account uses Email:FromAddress and a second one its Username.
    /// </summary>
    public string? FromAddress { get; set; }
}

/// <summary>One mailbox the site can send from, with the sender it sends as.</summary>
public sealed record SmtpAccount(int Number, SmtpSettings Settings, string From);

/// <summary>Configuration section "Email". Credentials come from environment variables / secrets only.</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>Unset: Log in Development, Disabled elsewhere (production validation requires an explicit choice).</summary>
    public EmailMode? Mode { get; set; }
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "PRYZMA";
    public string? PickupDirectory { get; set; }
    public SmtpSettings Smtp { get; set; } = new();

    /// <summary>
    /// A second mailbox, optional (Email__Smtp2__*). With two, messages take turns between them, so each carries half
    /// the load; one that reaches its provider's sending limit rests and the other carries on.
    /// </summary>
    public SmtpSettings Smtp2 { get; set; } = new();

    /// <summary>How long a mailbox that reported its sending limit is passed over before it is tried again.</summary>
    public int SmtpRestMinutes { get; set; } = 60;

    /// <summary>The mailboxes that are configured, first one first.</summary>
    public IReadOnlyList<SmtpAccount> SmtpAccounts()
    {
        var accounts = new List<SmtpAccount>();
        if (!string.IsNullOrWhiteSpace(Smtp.Host))
            accounts.Add(new SmtpAccount(1, Smtp, Smtp.FromAddress ?? FromAddress ?? Smtp.Username ?? "no-reply@localhost"));
        if (!string.IsNullOrWhiteSpace(Smtp2.Host))
            accounts.Add(new SmtpAccount(2, Smtp2, Smtp2.FromAddress ?? Smtp2.Username ?? FromAddress ?? "no-reply@localhost"));
        return accounts;
    }

    // Durable outbox (PostgreSQL) — see EmailOutboxWorker.
    /// <summary>Delivery attempts per message before it is marked Failed.</summary>
    public int MaxAttempts { get; set; } = 5;
    /// <summary>First retry delay; each further retry waits three times longer (capped at one hour).</summary>
    public int RetryDelaySeconds { get; set; } = 5;
    /// <summary>How often the worker looks for due messages (new ones are picked up at once in the same process).</summary>
    public int PollSeconds { get; set; } = 5;
    /// <summary>How long a claimed row stays locked to one worker; must exceed the SMTP timeout.</summary>
    public int LeaseSeconds { get; set; } = 120;
    public int BatchSize { get; set; } = 10;
    /// <summary>Finished rows (sent, failed, expired, cancelled) are deleted after this many days.</summary>
    public int RetentionDays { get; set; } = 30;
    /// <summary>Messages without their own validity (e.g. receipts) are dropped if still undelivered after this many days.</summary>
    public int DefaultValidityDays { get; set; } = 7;
    /// <summary>Only for tests / maintenance: false leaves messages pending.</summary>
    public bool OutboxWorkerEnabled { get; set; } = true;

    public EmailMode EffectiveMode(IHostEnvironment env) => Mode ?? (env.IsDevelopment() ? EmailMode.Log : EmailMode.Disabled);
}

public static class EmailAddresses
{
    /// <summary>"aysel@example.com" → "a***@example.com" — enough for support, useless for harvesting.</summary>
    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }
}

internal static class Mime
{
    public static MimeMessage Build(EmailMessage m, EmailOptions o, string? from = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, from ?? o.FromAddress ?? "no-reply@localhost"));
        message.To.Add(MailboxAddress.Parse(m.To));
        // The sender stays our own address (SPF/DKIM); a reply goes to the person the message is from.
        if (!string.IsNullOrWhiteSpace(m.ReplyTo))
            message.ReplyTo.Add(new MailboxAddress(m.ReplyToName ?? "", m.ReplyTo));
        message.Subject = m.Subject;
        message.Body = new BodyBuilder { HtmlBody = m.HtmlBody, TextBody = m.TextBody }.ToMessageBody();
        return message;
    }
}

/// <summary>Hands one message to one mailbox's SMTP server.</summary>
public interface ISmtpTransport
{
    Task SendAsync(SmtpSettings account, MimeMessage message, CancellationToken ct);
}

public sealed class MailKitSmtpTransport : ISmtpTransport
{
    public async Task SendAsync(SmtpSettings s, MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient { Timeout = s.TimeoutSeconds * 1000 };
        var socket = s.Security switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(s.Host ?? throw new InvalidOperationException("Email:Smtp:Host is not set."), s.Port, socket, ct);
        if (!string.IsNullOrEmpty(s.Username))
            await client.AuthenticateAsync(s.Username, s.Password ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}

/// <summary>
/// Sends through one or two mailboxes (Email:Smtp, Email:Smtp2). Messages take turns between them, so each mailbox
/// carries half the day's load. A mailbox whose provider says it has reached its sending limit (Gmail: "5.4.5 Daily
/// user sending limit exceeded") rests for Email:SmtpRestMinutes and the message goes through the other one at once;
/// any other failure of one mailbox also falls through to the other. Only when every mailbox has failed does the
/// message fail, and the outbox tries it again later. A recipient the server refuses is the address's fault, not the
/// mailbox's, so it is not tried again elsewhere.
/// </summary>
public sealed class SmtpEmailSender(
    IOptions<EmailOptions> options, ISmtpTransport transport, TimeProvider clock, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private int _turn = -1;
    private readonly ConcurrentDictionary<int, DateTimeOffset> _restingUntil = new();

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        var accounts = o.SmtpAccounts();
        if (accounts.Count == 0) throw new InvalidOperationException("Email:Smtp:Host is not set.");

        var now = clock.GetUtcNow();
        var first = (int)((uint)Interlocked.Increment(ref _turn) % (uint)accounts.Count);
        // This message's turn first, then the others; a resting mailbox last — still tried when it is all there is,
        // because a rest is a guess about the provider's limit, not a ban.
        var order = Enumerable.Range(0, accounts.Count)
            .Select(i => accounts[(first + i) % accounts.Count])
            .OrderBy(a => _restingUntil.TryGetValue(a.Number, out var until) && until > now ? 1 : 0)
            .ToList();

        Exception? last = null;
        foreach (var account in order)
        {
            try
            {
                await transport.SendAsync(account.Settings, Mime.Build(message, o, account.From), ct);
                _restingUntil.TryRemove(account.Number, out _);
                return;
            }
            catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                if (IsSendingLimit(ex))
                {
                    var until = clock.GetUtcNow().AddMinutes(Math.Max(1, o.SmtpRestMinutes));
                    _restingUntil[account.Number] = until;
                    logger.LogWarning("E-mail account {Account} reached its sending limit ({Status}); resting until {Until}",
                        account.Number, Status(ex), until);
                }
                else
                {
                    logger.LogWarning("E-mail account {Account} could not send ({Error}{Status})",
                        account.Number, ex.GetType().Name, Status(ex));
                }
            }
        }
        throw last!;
    }

    private static readonly string[] LimitWords = ["limit", "quota", "exceeded", "5.4.5", "4.7.0", "too many", "rate"];

    /// <summary>The provider refuses more mail from this mailbox for now — not a fault of this message.</summary>
    public static bool IsSendingLimit(Exception ex) =>
        ex is SmtpCommandException sce
        && (int)sce.StatusCode is 421 or 450 or 451 or 452 or 454 or 550 or 552 or 554
        && LimitWords.Any(w => sce.Message.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static string Status(Exception ex) => ex is SmtpCommandException sce ? $":{(int)sce.StatusCode}" : "";
}

/// <summary>Writes each message as an .eml file. Refused in Production by configuration validation.</summary>
public sealed class PickupDirectoryEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var dir = options.Value.PickupDirectory ?? throw new InvalidOperationException("Email:PickupDirectory is not set.");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.eml");
        // Written under a temporary name and renamed when complete: a pickup reader never sees a half-written .eml.
        var temp = path + ".tmp";
        await Mime.Build(message, options.Value).WriteToAsync(temp, ct);
        File.Move(temp, path);
    }
}

/// <summary>Development only (see DependencyInjection): the text body — reset links included — goes to the log.</summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogInformation("[DEV e-mail] {Kind} to {To}: {Subject}\n{Body}", message.Kind, message.To, message.Subject, message.TextBody);
        return Task.CompletedTask;
    }
}

public sealed class DisabledEmailSender(ILogger<DisabledEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogWarning("E-mail not sent (Email:Mode is Disabled): {Kind} to {Recipient}", message.Kind, EmailAddresses.Mask(message.To));
        return Task.CompletedTask;
    }
}
