using Meydan.Api.Features.Auth;

namespace Meydan.Api.Tests.Infrastructure;

/// <summary>
/// DNS for the registration gate, without the network: every domain has an ordinary mail exchanger unless a test says
/// otherwise in <see cref="Answers"/> (null = DNS gave no answer). Counts the lookups, for the gate's caching tests.
/// </summary>
public sealed class FakeMailDns : IMailDnsLookup
{
    public static readonly MailDns Ordinary = new(["mx.example.test"], ["203.0.113.10"], HasAddress: true);

    public Dictionary<string, MailDns?> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Lookups { get; } = [];

    public Task<MailDns?> LookupAsync(string domain, CancellationToken ct)
    {
        lock (Lookups) Lookups.Add(domain);
        return Task.FromResult(Answers.TryGetValue(domain, out var a) ? a : Ordinary);
    }
}
