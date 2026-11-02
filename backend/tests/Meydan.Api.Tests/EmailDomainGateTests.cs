using Meydan.Api.Features.Admin;
using Meydan.Api.Features.Auth;
using Meydan.Api.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meydan.Api.Tests;

/// <summary>The registration gate on its own: the lists, the DNS rule, the cache. No network, no host.</summary>
public sealed class EmailDomainGateTests
{
    private static (EmailDomainGate Gate, FakeMailDns Dns) Make()
    {
        var dns = new FakeMailDns();
        return (new EmailDomainGate(dns, new MemoryCache(new MemoryCacheOptions()), NullLogger<EmailDomainGate>.Instance), dns);
    }

    [Fact]
    public void The_public_list_is_embedded_and_large_and_the_D14_set_is_a_subset_of_it_but_for_three()
    {
        Assert.True(PublicDisposableDomains.Count > 90_000, PublicDisposableDomains.Count.ToString());
        Assert.True(PublicDisposableDomains.Contains("mailinator.com"));
        Assert.False(PublicDisposableDomains.Contains("gmail.com"));
        Assert.False(PublicDisposableDomains.Contains("mail.ru"));
        var missing = DisposableEmailRule.Domains.Keys.Where(d => !PublicDisposableDomains.Contains(d)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["3r.nexushint.com", "en2ch.com", "personalmail.fun"], missing);
    }

    [Theory]
    [InlineData("abowned.com", true)]            // D14
    [InlineData("x.abowned.com", true)]          // a subdomain of a listed domain is that domain
    [InlineData("mailinator.com", true)]         // the public list
    [InlineData("en2ch.com", true)]              // D14 v4
    [InlineData("gmail.com", false)]
    [InlineData("bda.edu.az", false)]
    [InlineData("abowned.community", false)]     // a look-alike is not a match
    public void A_listed_domain_or_a_subdomain_of_one_is_disposable_without_dns(string domain, bool listed)
    {
        Assert.Equal(listed, EmailDomainGate.IsListed(domain));
    }

    [Fact]
    public async Task A_listed_domain_never_reaches_dns()
    {
        var (gate, dns) = Make();
        Assert.Equal(EmailDomainVerdict.Disposable, await gate.CheckAsync("Someone@ABOWNED.com", CancellationToken.None));
        Assert.Empty(dns.Lookups);
    }

    [Theory]
    [InlineData(new string[0], new string[0], false, false, false, EmailDomainVerdict.NoMailServer)]   // NXDOMAIN or nothing at all
    [InlineData(new string[0], new string[0], true, false, false, EmailDomainVerdict.Ok)]              // no MX, but an address (RFC 5321 fallback)
    [InlineData(new string[0], new string[0], true, true, false, EmailDomainVerdict.NoMailServer)]     // "no mail here", RFC 7505
    [InlineData(new[] { "aspmx.l.google.com" }, new[] { "142.250.1.26" }, true, false, false, EmailDomainVerdict.Ok)]
    [InlineData(new[] { "mail.en2ch.example" }, new[] { "203.0.113.5" }, false, false, false, EmailDomainVerdict.NoWebsite)] // a mail server and nothing else: en2ch.com
    [InlineData(new[] { "mail.shop.example" }, new[] { "203.0.113.6" }, false, false, true, EmailDomainVerdict.Ok)]          // the site lives at www.
    [InlineData(new[] { "mail.wabblywabble.com" }, new[] { "188.166.111.252" }, true, false, false, EmailDomainVerdict.Disposable)]
    [InlineData(new[] { "mx2.YOPMAIL.com" }, new string[0], true, false, false, EmailDomainVerdict.Ok)] // exchangers arrive lower-cased; a mixed-case one is not matched blindly
    [InlineData(new[] { "mail.brand-new-domain.example" }, new[] { "95.216.191.158" }, true, false, false, EmailDomainVerdict.Disposable)] // temp-mail.io's server
    [InlineData(new[] { "notwabblywabble.com" }, new string[0], true, false, false, EmailDomainVerdict.Ok)]  // a suffix match needs the dot
    public void The_dns_rule(string[] mx, string[] addresses, bool hasAddress, bool nullMx, bool hasWww, EmailDomainVerdict expected)
    {
        Assert.Equal(expected, EmailDomainGate.Judge(new MailDns(mx, addresses, hasAddress, nullMx, hasWww)));
    }

    [Fact]
    public async Task A_big_provider_never_reaches_dns_or_the_lists()
    {
        var (gate, dns) = Make();
        dns.Answers["gmail.com"] = new MailDns([], [], HasAddress: false); // DNS that would refuse, if it were asked
        Assert.Equal(EmailDomainVerdict.Ok, await gate.CheckAsync("someone@gmail.com", CancellationToken.None));
        Assert.Equal(EmailDomainVerdict.Ok, await gate.CheckAsync("someone@Mail.ru", CancellationToken.None));
        Assert.Empty(dns.Lookups);
        Assert.All(EmailDomainGate.TrustedDomains, d => Assert.False(EmailDomainGate.IsListed(d), d));
    }

    [Fact]
    public async Task A_verdict_is_cached_per_domain_but_a_missing_answer_is_not()
    {
        var (gate, dns) = Make();
        dns.Answers["typo.example"] = new MailDns([], [], HasAddress: false);
        dns.Answers["silent.example"] = null;

        Assert.Equal(EmailDomainVerdict.NoMailServer, await gate.CheckAsync("a@typo.example", CancellationToken.None));
        Assert.Equal(EmailDomainVerdict.NoMailServer, await gate.CheckAsync("b@TYPO.example", CancellationToken.None));
        Assert.Equal(EmailDomainVerdict.Ok, await gate.CheckAsync("c@fine.example", CancellationToken.None));
        Assert.Equal(EmailDomainVerdict.Ok, await gate.CheckAsync("d@fine.example", CancellationToken.None));
        Assert.Equal(["typo.example", "fine.example"], dns.Lookups);

        Assert.Equal(EmailDomainVerdict.Ok, await gate.CheckAsync("e@silent.example", CancellationToken.None));
        Assert.Equal(EmailDomainVerdict.Ok, await gate.CheckAsync("f@silent.example", CancellationToken.None));
        Assert.Equal(["typo.example", "fine.example", "silent.example", "silent.example"], dns.Lookups);
    }
}
