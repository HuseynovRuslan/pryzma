namespace Meydan.Api.Features.Admin;

/// <summary>
/// D14, disposable e-mail during the vote (owner, 7 October 2026; docs/voting/OWNER-DECISIONS.md): the vote of an
/// account opened during the competition's voting window whose e-mail domain is one of a fixed set of disposable /
/// temporary e-mail services is set aside — by an admin, with a reason, audited, never deleted, the same for every work.
/// <para>
/// The set is frozen: the 37 domains the read-only audit of 7 October 2026 confirmed, each by a public classification
/// (check-mail.org, domain page) and by its mail exchanger belonging to the named service. Nothing is looked up when the
/// rule is applied, so the same votes meet it on every reading. A domain is matched exactly — no guessing, no
/// subdomains, no look-alikes: a Gmail typo is not a disposable service, and fusioninbox.com (one source only, operator
/// unknown) was left out by the owner.
/// </para>
/// <para>
/// Version 2 (owner, 2 November 2026): three 10minutemail.com domains seen in the Novxanı and Maştağa votes, each with
/// prd-smtp.10minutemail.com as its mail exchanger, join the set (40 domains). The same set now also closes
/// registration (<see cref="Auth.EmailFirstRegistration"/>): an address on it cannot open an account at all.
/// </para>
/// <para>
/// Version 3 (owner, 2 November 2026, the same day): yzcalo.com, 37xiao.com and hiringview.com join (43 domains) on the
/// owner's decision, without a public classification: yzcalo.com had no DNS left, the other two have no website and one
/// shared mail host (157.230.65.184); all three voted only for the works the 10minutemail accounts voted for.
/// </para>
/// <para>
/// Nothing else counts — not the voter's connection, name, speed, the work voted for or its place. Those stay signs
/// for review. The phone-cluster rule and R2 (D12) are unchanged.
/// </para>
/// Shown to admins always; ground to set a vote aside only while <see cref="Enabled"/>
/// (<c>Voting:DisposableEmail:Enabled</c>, off by default, as R2).
/// </summary>
public sealed record DisposableEmailRule(bool Enabled = false)
{
    public const string PolicyId = "D14_DISPOSABLE_EMAIL";
    public const int Version = 3;
    public const string EvidenceType = "DisposableEmailDuringVoting";

    /// <summary>The owner-approved reason, as the votes and the audit record it (admins only; the public reads the rule).</summary>
    public const string Reason = "Disposable/temporary email domain used by an account created during the voting window.";

    public const string Source =
        "Read-only audit 2026-10-07: check-mail.org domain classification + MX records of the named service; owner decision 2026-10-07. " +
        "v2 2026-11-02: three domains whose MX is prd-smtp.10minutemail.com; owner decision 2026-11-02. " +
        "v3 2026-11-02: three domains on the owner's decision alone (no DNS / no website, one shared mail host).";

    /// <summary>The frozen evidence set: domain → the disposable service behind it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Domains = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // temporarymail.com — one mail exchanger for all nine (51.38.251.14)
        ["inboxorigin.com"] = "temporarymail.com",
        ["mycreativeinbox.com"] = "temporarymail.com",
        ["easymailer.live"] = "temporarymail.com",
        ["mailmagnet.co"] = "temporarymail.com",
        ["horizonspost.com"] = "temporarymail.com",
        ["solarnyx.com"] = "temporarymail.com",
        ["allwebemails.com"] = "temporarymail.com",
        ["openmail.pro"] = "temporarymail.com",
        ["allfreemail.net"] = "temporarymail.com",
        // temp-mail.org
        ["abowned.com"] = "temp-mail.org",
        ["aminavin.com"] = "temp-mail.org",
        ["flakeian.com"] = "temp-mail.org",
        ["hudzer.com"] = "temp-mail.org",
        ["deertees.com"] = "temp-mail.org",
        // mail.td
        ["end.tw"] = "mail.td",
        ["nqmo.com"] = "mail.td",
        ["qabq.com"] = "mail.td",
        ["sugtbt.com"] = "mail.td",
        // temp-mail.io
        ["olipii.com"] = "temp-mail.io",
        ["tanpony.com"] = "temp-mail.io",
        ["gmeenramy.com"] = "temp-mail.io",
        ["lnovic.com"] = "temp-mail.io",
        ["ruutukf.com"] = "temp-mail.io",
        // mailporary.com
        ["mfxis.com"] = "mailporary.com",
        ["suarj.com"] = "mailporary.com",
        ["anogz.com"] = "mailporary.com",
        // freetemporarymail.com
        ["personalmail.fun"] = "freetemporarymail.com",
        ["missyoutoo.fun"] = "freetemporarymail.com",
        // one domain each
        ["maxxspace.com"] = "mail.tm",
        ["jbsze.com"] = "10minutemail.com",
        ["necub.com"] = "tempail.com",
        ["phuturemail.com"] = "xeramail.com",
        ["echoharbor.in"] = "freecustom.email",
        ["3r.nexushint.com"] = "tempmail.lol",
        ["onetopclick.online"] = "noopmail.org",
        ["animatimg.com"] = "tempmail.ing",
        ["betterr.org"] = "temp-mail.app",
        // v2 (2026-11-02): 10minutemail.com — mail exchanger prd-smtp.10minutemail.com for all three
        ["onldm.net"] = "10minutemail.com",
        ["vtmpj.com"] = "10minutemail.com",
        ["vtmpj.net"] = "10minutemail.com",
        // v3 (2026-11-02): the owner's decision; the service behind them is not known
        ["yzcalo.com"] = "unknown (no DNS on 2026-11-02)",
        ["37xiao.com"] = "unknown (mail host 157.230.65.184)",
        ["hiringview.com"] = "unknown (mail host 157.230.65.184)",
    };

    public static DisposableEmailRule From(IConfiguration config) =>
        new(config.GetSection("Voting:DisposableEmail").GetValue("Enabled", false));

    /// <summary>The e-mail's domain, normalized: trimmed, lower case, after the last "@", without a trailing dot.</summary>
    public static string? Domain(string? email)
    {
        var e = (email ?? "").Trim();
        var at = e.LastIndexOf('@');
        if (at < 0 || at == e.Length - 1) return null;
        var domain = e[(at + 1)..].ToLowerInvariant().TrimEnd('.');
        return domain.Length == 0 ? null : domain;
    }

    /// <summary>The domain of the frozen set this e-mail uses, or null.</summary>
    public static string? ListedDomain(string? email) => Domain(email) is { } d && Domains.ContainsKey(d) ? d : null;

    /// <summary>
    /// The rule itself: the account was opened inside the voting window (<c>[starts, ends)</c>) and its e-mail domain is
    /// in the frozen set. Accounts opened before the vote never meet it.
    /// </summary>
    public static bool Meets(string? email, DateTimeOffset accountCreatedAt, DateTimeOffset? votingStartsAt, DateTimeOffset? votingEndsAt) =>
        votingStartsAt is { } start && votingEndsAt is { } end
        && accountCreatedAt >= start && accountCreatedAt < end
        && ListedDomain(email) is not null;

    /// <summary>"a***@inboxorigin.com": enough for an admin to tell accounts apart, never the address.</summary>
    public static string Mask(string? email)
    {
        var e = (email ?? "").Trim();
        var at = e.LastIndexOf('@');
        return at <= 0 ? "***" : $"{e[0]}***@{Domain(e)}";
    }
}
