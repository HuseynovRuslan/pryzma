using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Meydan.Api.Tests.Infrastructure;

/// <summary>Records every formatted log line (and exception text) so tests can assert what never gets logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Lines);

    public void Dispose() { }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>Keeps sent e-mails in memory instead of delivering them; can simulate an unreachable mail server.</summary>
public sealed class CapturingEmailSender : Meydan.Infrastructure.Email.IEmailSender
{
    public ConcurrentQueue<Meydan.Infrastructure.Email.EmailMessage> Sent { get; } = new();
    public int Attempts;
    /// <summary>Throw on every attempt (SMTP down).</summary>
    public bool Fail { get; set; }
    /// <summary>Simulated SMTP round trip, so concurrent workers really overlap.</summary>
    public TimeSpan Delay { get; set; }
    /// <summary>Runs during each send (e.g. to move the test clock as a slow SMTP server would).</summary>
    public Action? DuringSend { get; set; }

    public async Task SendAsync(Meydan.Infrastructure.Email.EmailMessage message, CancellationToken ct)
    {
        Interlocked.Increment(ref Attempts);
        DuringSend?.Invoke();
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Fail) throw new System.Net.Sockets.SocketException(10061);
        Sent.Enqueue(message);
    }

    /// <summary>Background delivery: waits until a message of the kind arrives (or fails the test).</summary>
    public async Task<Meydan.Infrastructure.Email.EmailMessage> WaitForAsync(string kind, string to)
    {
        for (var i = 0; i < 100; i++)
        {
            var hit = Sent.FirstOrDefault(m => m.Kind == kind && m.To == to);
            if (hit is not null) return hit;
            await Task.Delay(50);
        }
        throw new TimeoutException($"No {kind} e-mail to {to}.");
    }
}
