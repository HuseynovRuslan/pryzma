using MailKit.Net.Smtp;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Meydan.Api.Tests;

/// <summary>
/// Two mailboxes instead of one (Email:Smtp, Email:Smtp2): messages take turns between them, a mailbox at its
/// provider's daily sending limit rests while the other carries on, and a refused recipient is not tried again
/// elsewhere. Without the network: the SMTP hand-over is a fake that records which mailbox sent what.
/// </summary>
public sealed class SmtpMailboxesTests
{
    private sealed class FakeTransport : ISmtpTransport
    {
        public List<(string Account, string From)> Sent { get; } = [];
        /// <summary>What a mailbox answers instead of sending, by its username.</summary>
        public Dictionary<string, Func<Exception>> Refuse { get; } = [];
        public List<string> Tried { get; } = [];

        public Task SendAsync(SmtpSettings account, MimeMessage message, CancellationToken ct)
        {
            Tried.Add(account.Username!);
            if (Refuse.TryGetValue(account.Username!, out var refusal)) throw refusal();
            Sent.Add((account.Username!, message.From.Mailboxes.Single().Address));
            return Task.CompletedTask;
        }
    }

    private static readonly EmailMessage Message = new("voter@example.com", "Təsdiq", "<p>link</p>", "link", "email-confirmation");

    private static (SmtpEmailSender, FakeTransport, TestClock) Sender(bool two = true)
    {
        var o = new EmailOptions
        {
            Mode = EmailMode.Smtp,
            FromAddress = "pryzma.one@gmail.com",
            Smtp = new SmtpSettings { Host = "smtp.gmail.com", Username = "pryzma.one@gmail.com", Password = "x" },
        };
        if (two) o.Smtp2 = new SmtpSettings { Host = "smtp.gmail.com", Username = "pryzma.two@gmail.com", Password = "y" };
        var transport = new FakeTransport();
        var clock = new TestClock();
        return (new SmtpEmailSender(Options.Create(o), transport, clock, NullLogger<SmtpEmailSender>.Instance), transport, clock);
    }

    private static SmtpCommandException DailyLimit() => new(
        SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.MailboxUnavailable,
        "Daily user sending limit exceeded. For more information on Gmail sending limits go to https://support.google.com/a/answer/166852 5.4.5");

    [Fact]
    public async Task One_mailbox_sends_everything_as_before_from_the_configured_sender()
    {
        var (sender, transport, _) = Sender(two: false);
        await sender.SendAsync(Message, default);
        await sender.SendAsync(Message, default);
        Assert.Equal([("pryzma.one@gmail.com", "pryzma.one@gmail.com"), ("pryzma.one@gmail.com", "pryzma.one@gmail.com")], transport.Sent);
    }

    [Fact]
    public async Task Two_mailboxes_take_turns_each_sending_as_itself()
    {
        var (sender, transport, _) = Sender();
        for (var i = 0; i < 4; i++) await sender.SendAsync(Message, default);
        Assert.Equal(
            [
                ("pryzma.one@gmail.com", "pryzma.one@gmail.com"), ("pryzma.two@gmail.com", "pryzma.two@gmail.com"),
                ("pryzma.one@gmail.com", "pryzma.one@gmail.com"), ("pryzma.two@gmail.com", "pryzma.two@gmail.com"),
            ],
            transport.Sent);
    }

    [Fact]
    public async Task A_mailbox_at_its_daily_limit_rests_and_the_other_carries_the_mail_until_the_rest_is_over()
    {
        var (sender, transport, clock) = Sender();
        transport.Refuse["pryzma.one@gmail.com"] = DailyLimit;

        // Its turn: refused for the limit, so the same message goes through the second mailbox at once.
        await sender.SendAsync(Message, default);
        Assert.Equal(["pryzma.one@gmail.com", "pryzma.two@gmail.com"], transport.Tried);

        // While it rests it is not asked first, even on its turn.
        transport.Tried.Clear();
        for (var i = 0; i < 3; i++) await sender.SendAsync(Message, default);
        Assert.Equal(["pryzma.two@gmail.com", "pryzma.two@gmail.com", "pryzma.two@gmail.com"], transport.Tried);
        Assert.Equal(4, transport.Sent.Count);

        // After the rest (an hour by default) it is asked again; its limit has lifted.
        transport.Refuse.Clear();
        transport.Tried.Clear();
        clock.Now = clock.Now.AddMinutes(61);
        await sender.SendAsync(Message, default);
        await sender.SendAsync(Message, default);
        Assert.Contains("pryzma.one@gmail.com", transport.Tried);
    }

    [Fact]
    public async Task Any_other_failure_of_one_mailbox_falls_through_to_the_other()
    {
        var (sender, transport, _) = Sender();
        transport.Refuse["pryzma.one@gmail.com"] = () => new MailKit.Security.AuthenticationException("bad password");
        await sender.SendAsync(Message, default);
        Assert.Equal([("pryzma.two@gmail.com", "pryzma.two@gmail.com")], transport.Sent);
    }

    [Fact]
    public async Task A_refused_recipient_is_the_address_not_the_mailbox_and_is_not_tried_elsewhere()
    {
        var (sender, transport, _) = Sender();
        transport.Refuse["pryzma.one@gmail.com"] = () =>
            new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "5.1.1 The email account does not exist");
        await Assert.ThrowsAsync<SmtpCommandException>(() => sender.SendAsync(Message, default));
        Assert.Equal(["pryzma.one@gmail.com"], transport.Tried);
    }

    [Fact]
    public async Task When_every_mailbox_fails_the_message_fails_and_the_outbox_tries_it_later()
    {
        var (sender, transport, _) = Sender();
        transport.Refuse["pryzma.one@gmail.com"] = DailyLimit;
        transport.Refuse["pryzma.two@gmail.com"] = DailyLimit;
        await Assert.ThrowsAsync<SmtpCommandException>(() => sender.SendAsync(Message, default));
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void A_second_mailbox_without_its_own_sender_sends_as_its_username()
    {
        var o = new EmailOptions
        {
            FromAddress = "no-reply@pryzma.az",
            Smtp = new SmtpSettings { Host = "smtp.example.com" },
            Smtp2 = new SmtpSettings { Host = "smtp.gmail.com", Username = "pryzma.two@gmail.com" },
        };
        Assert.Equal(["no-reply@pryzma.az", "pryzma.two@gmail.com"], o.SmtpAccounts().Select(a => a.From));
        Assert.Single(new EmailOptions { Smtp = new SmtpSettings { Host = "h" } }.SmtpAccounts());
    }

    [Fact]
    public void Only_a_refusal_that_speaks_of_a_limit_counts_as_one()
    {
        Assert.True(SmtpEmailSender.IsSendingLimit(DailyLimit()));
        Assert.True(SmtpEmailSender.IsSendingLimit(new SmtpCommandException(
            SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.ServiceNotAvailable, "4.7.0 Try again later, closing connection.")));
        Assert.False(SmtpEmailSender.IsSendingLimit(new SmtpCommandException(
            SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, "5.7.1 Message rejected as spam")));
        Assert.False(SmtpEmailSender.IsSendingLimit(new TimeoutException()));
    }
}
