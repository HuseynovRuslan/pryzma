using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meydan.Api.Common;

namespace Meydan.Api.Features.Admin;

/// <summary>One vote as the suspicion score reads it: who cast it, for which work, and the work's author.</summary>
/// <param name="SignUpIp">The address the voter's account was opened from (its first sign-in), when known.</param>
public sealed record SuspicionVote(
    long VoteId, int SubmissionId, string Reference, Guid VoterId, string VoterFirst, string VoterLast,
    string? VoterPhone, string? VoterEmail, DateTimeOffset SignedUpAt, DateTimeOffset VotedAt,
    string AuthorFirst, string AuthorLast, string? AuthorPhone, IReadOnlyCollection<string> VoterIps,
    string? SignUpIp = null);

/// <summary>
/// How strong the case against a vote is (docs/voting/OWNER-DECISIONS.md, D12, as narrowed by the owner on
/// 5 October 2026). Only <see cref="PhoneCluster"/> may support setting a vote aside; nothing sets one aside on its own.
/// </summary>
public enum VoteEvidence
{
    /// <summary>No sign at all.</summary>
    None,
    /// <summary>
    /// Review only — every other sign, however strong it looks: a repeated name (even on three or more new accounts),
    /// the author's own phone, a shared connection or the author's, timing, bursts, a fast sign-up-to-vote, a
    /// look-alike e-mail, a phone shared by just two accounts. Worth a look, never enough to set a vote aside.
    /// </summary>
    Weak,
    /// <summary>
    /// R2, coordinated identity — only while the owner has switched it on (<see cref="CoordinatedIdentityRule"/>):
    /// an account opened during the vote whose full name many voting accounts share, enough of them opened from the
    /// voter's own registration address. Switched off, the same votes are <see cref="Weak"/> with the sign in their reasons.
    /// </summary>
    CoordinatedIdentity,
    /// <summary>
    /// The one objective rule: the voter's normalized phone is held by <see cref="VoteSuspicion.PhoneClusterSize"/> or
    /// more voting accounts of the competition, counted competition-wide.
    /// </summary>
    PhoneCluster,
    /// <summary>
    /// D14 — only while the owner's rule is switched on (<see cref="DisposableEmailRule"/>): the voter's account was
    /// opened during the vote with an e-mail domain of the frozen disposable set. A phone cluster or R2 keeps its own
    /// evidence when a vote meets both.
    /// </summary>
    DisposableEmailDuringVoting,
}

/// <param name="PhoneGroup">Voting accounts of the competition with this voter's phone, this one included; 0 without a phone.</param>
/// <param name="NewNameGroup">Accounts opened during the vote with this voter's name, this one included; 0 if this one is not new.</param>
/// <param name="NameGroup">Voting accounts of the competition with this voter's name, this one included; 0 for a name too short to compare.</param>
/// <param name="SignUpIpInNameGroup">Of <paramref name="NameGroup"/>, the accounts opened from this voter's registration address.</param>
/// <param name="CoordinatedIdentity">Meets R2, whether or not the rule is switched on.</param>
public sealed record ScoredVote(
    SuspicionVote Vote, int Score, IReadOnlyList<string> Reasons,
    VoteEvidence Evidence = VoteEvidence.None, int PhoneGroup = 0, int NewNameGroup = 0, bool AuthorPhone = false,
    int NameGroup = 0, int SignUpIpInNameGroup = 0, bool CoordinatedIdentity = false);

/// <summary>
/// R2, coordinated identity — proposed after the post-hardening audit of 6 October 2026, not yet part of D12. A vote
/// meets it when all of these hold:
/// <list type="bullet">
/// <item>the voter's account was opened during the vote;</item>
/// <item>its normalized full name is held by <see cref="NameGroupMin"/> or more voting accounts of the competition;</item>
/// <item><see cref="SameIpMin"/> or more of those accounts were opened from this voter's registration address — or,
/// only with <see cref="PhoneCorroborates"/>, the voter's phone is not a plausible Azerbaijani mobile.</item>
/// </list>
/// It is always worked out and shown on the review screen. It is ground to set a vote aside only with
/// <see cref="Enabled"/> — configuration <c>Voting:CoordinatedIdentity:Enabled</c>, off unless the owner adopts it.
/// Thresholds below 3 are raised to 3: a pair is never a cluster.
/// </summary>
public sealed record CoordinatedIdentityRule(
    bool Enabled = false, int NameGroupMin = 5, int SameIpMin = 5, bool PhoneCorroborates = false)
{
    public static readonly CoordinatedIdentityRule Off = new();

    public static CoordinatedIdentityRule From(IConfiguration config)
    {
        var s = config.GetSection("Voting:CoordinatedIdentity");
        return new CoordinatedIdentityRule(
            s.GetValue("Enabled", false),
            Math.Max(3, s.GetValue("NameGroupMin", 5)),
            Math.Max(3, s.GetValue("SameIpMin", 5)),
            s.GetValue("PhoneCorroborates", false));
    }
}

/// <summary>
/// A suspicion score for every vote — 0 to 99, with the reasons that make it up. It is a weighted list of signs,
/// not a measured probability: there is no set of votes known to be fake to calibrate it against, and it says so
/// wherever it is shown. It never changes a vote; it helps an admin decide who gets a place.
/// <para>
/// The strongest signs are the ones a person cannot share by accident: the author's own phone on a voting account,
/// the author's own name, one phone or one name on two voting accounts. Weaker ones only add up: many accounts of
/// one work from one connection (mobile networks share addresses, so alone it proves little), look-alike e-mails,
/// an account opened during the vote and used at once, a burst of such accounts. Family is not fraud — a surname
/// that matches the author's adds almost nothing.
/// </para>
/// </summary>
public static class VoteSuspicion
{
    public const int High = 60;
    public const int Medium = 30;

    /// <summary>How many voting accounts must share one phone before their votes may be set aside (D12).</summary>
    public const int PhoneClusterSize = 3;

    public static IReadOnlyList<ScoredVote> Score(
        IReadOnlyList<SuspicionVote> votes, DateTimeOffset? votingStartsAt, CoordinatedIdentityRule? r2 = null)
    {
        r2 ??= CoordinatedIdentityRule.Off;
        var name = votes.ToDictionary(v => v.VoteId, v => $"{Norm(v.VoterFirst)} {Norm(v.VoterLast)}");
        var phone = votes.ToDictionary(v => v.VoteId, v => Phone(v.VoterPhone));
        var byPhone = votes.Where(v => phone[v.VoteId].Length > 0).ToLookup(v => phone[v.VoteId]);
        var byName = votes.ToLookup(v => name[v.VoteId]);
        var byWork = votes.ToLookup(v => v.SubmissionId);
        bool IsNew(SuspicionVote v) => votingStartsAt is { } start && v.SignedUpAt >= start;

        var scored = new List<ScoredVote>(votes.Count);
        foreach (var v in votes)
        {
            var score = 0;
            var why = new List<string>();
            void Add(int points, string reason)
            {
                score += points;
                why.Add(reason);
            }

            var ph = phone[v.VoteId];
            var authorPhone = ph.Length > 0 && ph == Phone(v.AuthorPhone);
            if (authorPhone)
                Add(90, "Telefonu işin müəllifi ilə eynidir — öz işinə ikinci hesabla səs ola bilər");
            if (Norm(v.VoterFirst) + Norm(v.VoterLast) == Norm(v.AuthorFirst) + Norm(v.AuthorLast))
                Add(80, "Adı işin müəllifinin adı ilə eynidir");

            var samePhone = ph.Length > 0 ? byPhone[ph].Where(o => o.VoterId != v.VoterId).ToList() : [];
            if (samePhone.Count > 0)
                Add(v.SignedUpAt > samePhone.Min(o => o.SignedUpAt) ? 60 : 35,
                    $"Telefonu səs verən başqa {samePhone.Count} hesabla eynidir");

            var sameName = byName[name[v.VoteId]].Where(o => o.VoterId != v.VoterId).ToList();
            if (sameName.Count > 0 && name[v.VoteId].Length > 4)
                Add(v.SignedUpAt > sameName.Min(o => o.SignedUpAt) ? 50 : 25,
                    $"Eyni ad-soyadla səs verən başqa {sameName.Count} hesab var");

            var peers = byWork[v.SubmissionId].Where(o => o.VoterId != v.VoterId).ToList();
            var shared = v.VoterIps.Count == 0 ? 0 : v.VoterIps.Max(ip => peers.Count(o => o.VoterIps.Contains(ip)));
            if (shared >= 8) Add(40, $"Bu işə eyni IP-dən daha {shared} hesab səs verib");
            else if (shared >= 3) Add(25, $"Bu işə eyni IP-dən daha {shared} hesab səs verib");
            else if (shared >= 1) Add(10, $"Bu işə eyni IP-dən daha {shared} hesab səs verib");

            var stem = Stem(v.VoterEmail);
            if (stem.Length >= 4 && peers.Any(o => Stem(o.VoterEmail) == stem))
                Add(20, "E-maili bu işin başqa səsverəninkinə oxşayır (yalnız rəqəmlər fərqlidir)");

            if (IsNew(v))
            {
                Add(10, "Hesab səsvermə başlayandan sonra açılıb");
                var gap = (v.VotedAt - v.SignedUpAt).TotalMinutes;
                if (gap < 2) Add(10, "Hesab açılan kimi (2 dəqiqə içində) səs verib");
                else if (gap < 10) Add(5, "Hesab açılandan 10 dəqiqə içində səs verib");
                var burst = byWork[v.SubmissionId].Count(o => IsNew(o) && Math.Abs((o.VotedAt - v.VotedAt).TotalMinutes) <= 7.5);
                if (burst >= 5) Add(10, $"Bu işə 15 dəqiqə içində {burst} yeni hesabdan səs gəlib");
            }

            var root = SurnameRoot(v.VoterLast);
            if (root.Length > 0 && root == SurnameRoot(v.AuthorLast) && Norm(v.VoterFirst) != Norm(v.AuthorFirst))
                Add(5, "Soyadı müəllifinkinə uyğundur (ailə üzvü ola bilər — bu qadağan deyil)");

            // D12: only a phone held by three or more voting accounts is evidence. A repeated name and the author's own
            // phone still raise the score and stay on the review screen — they are signs, not grounds.
            var phoneGroup = ph.Length > 0 ? byPhone[ph].Select(o => o.VoterId).Distinct().Count() : 0;
            var newNameGroup = IsNew(v) && name[v.VoteId].Length > 4
                ? byName[name[v.VoteId]].Where(IsNew).Select(o => o.VoterId).Distinct().Count()
                : 0;

            // R2 adds no points — the score stays what it was — but it is always named, so the reviewer sees it.
            var nm = name[v.VoteId];
            var nameGroup = nm.Length > 4 ? byName[nm].Select(o => o.VoterId).Distinct().Count() : 0;
            var ipInGroup = nameGroup > 0 && v.SignUpIp is { Length: > 0 } signUpIp
                ? byName[nm].Where(o => o.SignUpIp == signUpIp).Select(o => o.VoterId).Distinct().Count()
                : 0;
            var byAddress = ipInGroup >= r2.SameIpMin;
            var byPhone2 = !byAddress && r2.PhoneCorroborates && PhoneNumbers.AzMobile(v.VoterPhone) is null;
            var coordinated = IsNew(v) && nameGroup >= r2.NameGroupMin && (byAddress || byPhone2);
            if (coordinated)
                why.Add($"R2 (koordinasiyalı kimlik): yeni hesab; eyni ad-soyad {nameGroup} səsverən hesabdadır, " +
                        (byAddress
                            ? $"onlardan {ipInGroup}-i eyni IP-dən açılıb"
                            : "telefonu Azərbaycan mobil nömrəsi formasında deyil") +
                        (r2.Enabled ? "" : " — R2 hələ qəbul edilməyib, yalnız yoxlama əlamətidir"));

            var evidence = phoneGroup >= PhoneClusterSize ? VoteEvidence.PhoneCluster
                : coordinated && r2.Enabled ? VoteEvidence.CoordinatedIdentity
                : score > 0 || coordinated ? VoteEvidence.Weak : VoteEvidence.None;

            scored.Add(new ScoredVote(v, Math.Min(score, 99), why, evidence, phoneGroup, newNameGroup, authorPhone,
                nameGroup, ipInGroup, coordinated));
        }
        return scored;
    }

    // ---- Reading names, phones and e-mails the same way however they were typed -------------------

    private static readonly Dictionary<char, char> Latin = new()
    {
        ['ə'] = 'e', ['ı'] = 'i', ['ö'] = 'o', ['ü'] = 'u', ['ğ'] = 'g', ['ç'] = 'c', ['ş'] = 's',
    };

    /// <summary>"Fədayə" and "Fedaye" are one name: Azerbaijani letters to Latin, lower case, letters only.</summary>
    internal static string Norm(string? text)
    {
        var lower = (text ?? "").ToLower(CultureInfo.GetCultureInfo("az"));
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower.Normalize(NormalizationForm.FormD))
        {
            var c = Latin.TryGetValue(ch, out var l) ? l : ch;
            if (c is >= 'a' and <= 'z') sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The last nine digits: "+994 50 123 45 67", "050-123-45-67" and "0501234567" are one number.</summary>
    internal static string Phone(string? phone)
    {
        var digits = Regex.Replace(phone ?? "", @"\D", "");
        return digits.Length >= 9 ? digits[^9..] : "";
    }

    /// <summary>The letters of an e-mail's name part, without digits and separators: aynur.e1 and aynure22 match.</summary>
    internal static string Stem(string? email) =>
        Regex.Replace((email ?? "").Split('@')[0].ToLowerInvariant(), @"[0-9._\-]", "");

    private static readonly string[] Suffixes = ["yeva", "ova", "eva", "yev", "ov", "ev", "li", "lu", "zade", "zada"];

    /// <summary>"Həsənova" and "Həsənov" share a root; so do "Ələkbərli" and "Ələkbərova".</summary>
    internal static string SurnameRoot(string? surname)
    {
        var n = Norm(surname);
        foreach (var s in Suffixes)
            if (n.EndsWith(s, StringComparison.Ordinal) && n.Length - s.Length >= 3)
                return n[..^s.Length];
        return n;
    }
}
