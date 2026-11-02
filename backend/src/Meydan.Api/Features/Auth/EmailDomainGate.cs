using System.Collections.Frozen;
using System.Net.Sockets;
using DnsClient;
using Meydan.Api.Features.Admin;
using Microsoft.Extensions.Caching.Memory;

namespace Meydan.Api.Features.Auth;

/// <summary>What the registration gate says about an address's domain.</summary>
public enum EmailDomainVerdict
{
    Ok,
    /// <summary>A temporary / disposable mailbox service.</summary>
    Disposable,
    /// <summary>The domain does not exist or has no mail exchanger and no address: nothing can be delivered there.</summary>
    NoMailServer,
    /// <summary>
    /// A mail exchanger but no website: neither the domain nor www. under it has an address. Real mail providers have a
    /// site; a domain minted for throwaway mailboxes has none (owner, 2 November 2026).
    /// </summary>
    NoWebsite,
}

/// <summary>
/// What DNS says about a domain: its mail exchangers (lower case, no trailing dot, by preference, at most three), their
/// IPv4 addresses, whether the domain itself has an address, whether www. under it has one, and whether it declares
/// "no mail" (a null MX, RFC 7505).
/// </summary>
public sealed record MailDns(IReadOnlyList<string> MxHosts, IReadOnlyList<string> MxAddresses, bool HasAddress, bool NullMx = false, bool HasWww = false);

public interface IMailDnsLookup
{
    /// <summary>null when DNS could not answer (timeout, server failure): the gate then lets the address through.</summary>
    Task<MailDns?> LookupAsync(string domain, CancellationToken ct);
}

public interface IEmailDomainGate
{
    Task<EmailDomainVerdict> CheckAsync(string email, CancellationToken ct);
}

/// <summary>
/// The owner's rule of 2 November 2026: an address whose domain cannot receive mail, or belongs to a temporary mailbox
/// service, opens no account — it is refused before a link is queued or anything is stored. Three sources, in order:
/// the frozen D14 set (<see cref="DisposableEmailRule.Domains"/>), the public disposable-domain list embedded in the
/// assembly (<see cref="PublicDisposableDomains"/>), and DNS: a domain with no mail exchanger and no address, a mail
/// exchanger under a known temporary-mail service, a mail exchanger on a server shared by such a service, or a mail
/// exchanger without any website behind the domain. A parent domain counts too (x.abowned.com is abowned.com). The
/// big providers skip DNS altogether. DNS verdicts are cached per domain; when DNS does not answer, the address goes
/// through — a resolver hiccup must not close registration.
/// </summary>
public sealed class EmailDomainGate(IMailDnsLookup dns, IMemoryCache cache, ILogger<EmailDomainGate> logger) : IEmailDomainGate
{
    /// <summary>Providers nearly every participant uses: no lookup, no cache, no way for a DNS hiccup to touch them.</summary>
    public static readonly FrozenSet<string> TrustedDomains = new[]
    {
        "gmail.com", "googlemail.com", "mail.ru", "bk.ru", "list.ru", "inbox.ru", "internet.ru", "icloud.com", "me.com", "mac.com",
        "yahoo.com", "outlook.com", "hotmail.com", "live.com", "msn.com", "yandex.ru", "yandex.com", "ya.ru", "box.az", "mail.az",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Temporary-mail services whose every domain is a temporary mailbox: a domain whose mail exchanger is under one of
    /// these is refused, whatever the domain is called. The first five were read off the 2026 votes (temp-mail.org's
    /// wabblywabble.com / wallywatts.com, mail.td, mail.tm, 10minutemail.com); the rest are well-known services.
    /// </summary>
    public static readonly string[] BlockedMxSuffixes =
    [
        "wabblywabble.com", "wallywatts.com", "mail.td", "mail.tm", "10minutemail.com",
        "yopmail.com", "guerrillamail.com", "sharklasers.com", "grr.la", "mailinator.com", "temp-mail.org", "temp-mail.io",
        "tempmail.lol", "dispostable.com", "trashmail.com", "maildrop.cc", "getnada.com", "mohmal.com", "tempr.email",
        "emailondeck.com", "fakeinbox.com", "mailnesia.com", "moakt.com", "tempmailo.com", "mintemail.com", "throwawaymail.com",
    ];

    /// <summary>
    /// Mail servers shared by families of temporary domains: each domain gets its own exchanger name, the server stays.
    /// Resolved on 2 November 2026 from the domains that voted in "Novxanının simvolu" and "Maştağanın simvolu".
    /// </summary>
    public static readonly string[] BlockedMxAddresses =
    [
        "51.38.251.14",    // temporarymail.com (inboxorigin.com, mailmagnet.co, solarnyx.com …)
        "95.216.191.158",  // temp-mail.io (olipii.com, tanpony.com, lnovic.com …)
        "143.110.211.229", // mailporary.com (mfxis.com, anogz.com)
        "165.227.252.21",  // en2ch.com
        "188.166.111.252", "46.101.111.206", // temp-mail.org (wabblywabble.com, wallywatts.com)
        "188.40.237.153",  // mail.td
        "52.25.207.32",    // 10minutemail.com
    ];

    public async Task<EmailDomainVerdict> CheckAsync(string email, CancellationToken ct)
    {
        var domain = DisposableEmailRule.Domain(email);
        if (domain is null) return EmailDomainVerdict.Ok; // the request model's [EmailAddress] has already refused it
        if (TrustedDomains.Contains(domain)) return EmailDomainVerdict.Ok;
        if (IsListed(domain)) return EmailDomainVerdict.Disposable;

        var key = "email-domain:" + domain;
        if (cache.TryGetValue(key, out EmailDomainVerdict cached)) return cached;

        var answer = await dns.LookupAsync(domain, ct);
        if (answer is null)
        {
            logger.LogWarning("DNS did not answer for the domain of a registration address; the address is let through");
            return EmailDomainVerdict.Ok;
        }
        var verdict = Judge(answer);
        cache.Set(key, verdict, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = verdict == EmailDomainVerdict.Ok ? TimeSpan.FromHours(12) : TimeSpan.FromHours(24),
        });
        return verdict;
    }

    /// <summary>The domain, or a parent of it, is on the D14 set or the public list.</summary>
    public static bool IsListed(string domain)
    {
        var labels = domain.Split('.');
        for (var i = 0; i <= labels.Length - 2; i++)
        {
            var candidate = string.Join('.', labels, i, labels.Length - i);
            if (DisposableEmailRule.Domains.ContainsKey(candidate) || PublicDisposableDomains.Contains(candidate)) return true;
        }
        return false;
    }

    /// <summary>The DNS rule alone, for a domain that is on no list.</summary>
    public static EmailDomainVerdict Judge(MailDns m)
    {
        if (m.NullMx) return EmailDomainVerdict.NoMailServer;
        if (m.MxHosts.Count == 0 && !m.HasAddress) return EmailDomainVerdict.NoMailServer;
        if (m.MxHosts.Any(h => BlockedMxSuffixes.Any(s => h == s || h.EndsWith("." + s, StringComparison.Ordinal))))
            return EmailDomainVerdict.Disposable;
        if (m.MxAddresses.Any(a => BlockedMxAddresses.Contains(a))) return EmailDomainVerdict.Disposable;
        if (m.MxHosts.Count > 0 && !m.HasAddress && !m.HasWww) return EmailDomainVerdict.NoWebsite;
        return EmailDomainVerdict.Ok;
    }
}

/// <summary>
/// The public disposable-domain list (github.com/disposable/disposable-email-domains, domains.txt), embedded in the
/// assembly as it was on 2 November 2026: 98,464 domains. Registration only — D14 (vote invalidation) keeps its own
/// frozen, individually verified set.
/// </summary>
public static class PublicDisposableDomains
{
    private static readonly Lazy<FrozenSet<string>> Set = new(Load);

    public static bool Contains(string domain) => Set.Value.Contains(domain);

    public static int Count => Set.Value.Count;

    private static FrozenSet<string> Load()
    {
        using var stream = typeof(PublicDisposableDomains).Assembly.GetManifestResourceStream("disposable-domains.txt")
            ?? throw new InvalidOperationException("disposable-domains.txt is not embedded in Meydan.Api.");
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var d = line.Trim().ToLowerInvariant();
            if (d.Length > 0 && d[0] != '#') set.Add(d);
        }
        return set.ToFrozenSet(StringComparer.Ordinal);
    }
}

/// <summary>MX, then A for the exchangers (and for the domain when it has no exchanger), through the host's resolver.</summary>
public sealed class DnsClientMailLookup(ILogger<DnsClientMailLookup> logger) : IMailDnsLookup
{
    private readonly LookupClient _client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(2),
        Retries = 1,
        UseCache = true,
        ThrowDnsErrors = false,
    });

    public async Task<MailDns?> LookupAsync(string domain, CancellationToken ct)
    {
        try
        {
            var mx = await _client.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
            var missing = mx.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain;
            if (mx.HasError && !missing) return null; // server failure, refused…: no verdict

            var exchangers = mx.Answers.MxRecords().OrderBy(r => r.Preference)
                .Select(r => r.Exchange.Value.TrimEnd('.').ToLowerInvariant()).ToList();
            var nullMx = exchangers.Count > 0 && exchangers.All(h => h.Length == 0);
            var hosts = exchangers.Where(h => h.Length > 0).Take(3).ToList();

            var addresses = new List<string>();
            foreach (var host in hosts)
            {
                var a = await _client.QueryAsync(host, QueryType.A, cancellationToken: ct);
                addresses.AddRange(a.Answers.ARecords().Select(r => r.Address.ToString()));
            }

            // The website: an address on the domain itself, else on www. under it (a CNAME resolves to one).
            var hasAddress = !missing && await HasAddressAsync(domain, ct);
            var hasWww = !missing && !hasAddress && hosts.Count > 0 && await HasAddressAsync("www." + domain, ct);
            return new MailDns(hosts, addresses, hasAddress, nullMx, hasWww);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DnsResponseException or SocketException or OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("DNS lookup for a registration domain failed: {ErrorType}", ex.GetType().Name);
            return null;
        }
    }

    private async Task<bool> HasAddressAsync(string name, CancellationToken ct)
    {
        var a = await _client.QueryAsync(name, QueryType.A, cancellationToken: ct);
        if (a.Answers.ARecords().Any()) return true;
        var aaaa = await _client.QueryAsync(name, QueryType.AAAA, cancellationToken: ct);
        return aaaa.Answers.AaaaRecords().Any();
    }
}
