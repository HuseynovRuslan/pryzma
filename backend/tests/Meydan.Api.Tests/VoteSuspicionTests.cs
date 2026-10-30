using Meydan.Api.Features.Admin;

namespace Meydan.Api.Tests;

/// <summary>
/// The suspicion score, without a database: each sign alone, the way names and phones are read whatever way they
/// were typed, and a genuine vote that scores nothing.
/// </summary>
public sealed class VoteSuspicionTests
{
    private static readonly DateTimeOffset Opened = new(2026, 10, 4, 7, 40, 0, TimeSpan.Zero);
    private static long _id;

    private static SuspicionVote Vote(
        string first, string last, int work = 1, string? phone = null, string? email = null,
        double signedUpMinutes = -600, double votedMinutes = 60, string[]? ips = null,
        string authorFirst = "Roya", string authorLast = "Əliyeva", string? authorPhone = "+994 55 111 22 33") =>
        new(++_id, work, $"SUB-2026-{work:D5}", Guid.NewGuid(), first, last, phone ?? $"+994 70 {_id:D3} 00 00",
            email ?? $"{first.ToLowerInvariant()}{_id}x@example.com",
            Opened.AddMinutes(signedUpMinutes), Opened.AddMinutes(votedMinutes), authorFirst, authorLast, authorPhone,
            ips ?? [$"10.0.0.{_id % 250}"]);

    private static ScoredVote Of(IReadOnlyList<ScoredVote> all, SuspicionVote v) => all.Single(s => s.Vote.VoteId == v.VoteId);

    [Fact]
    public void A_genuine_vote_scores_nothing()
    {
        var v = Vote("Kamran", "Babayev");
        var s = Of(VoteSuspicion.Score([v], Opened), v);
        Assert.Equal(0, s.Score);
        Assert.Empty(s.Reasons);
    }

    [Fact]
    public void The_authors_own_phone_on_a_voting_account_is_almost_certainly_a_self_vote()
    {
        var v = Vote("Tofiq", "Vəliyev", phone: "055-111-22-33");     // the author's +994 55 111 22 33, typed differently
        var s = Of(VoteSuspicion.Score([v], Opened), v);
        Assert.True(s.Score >= 90);
        Assert.Contains(s.Reasons, r => r.StartsWith("Telefonu işin müəllifi ilə eynidir"));
    }

    [Fact]
    public void One_name_on_two_accounts_weighs_more_on_the_later_one_and_spelling_does_not_hide_it()
    {
        var first = Vote("Fədayə", "Qurbanova", signedUpMinutes: 100, votedMinutes: 101);
        var second = Vote("Fedaye", "Qurbanova", signedUpMinutes: 104, votedMinutes: 105);
        var all = VoteSuspicion.Score([first, second], Opened);
        Assert.Contains(Of(all, second).Reasons, r => r.StartsWith("Eyni ad-soyadla"));
        Assert.True(Of(all, second).Score > Of(all, first).Score);
    }

    [Fact]
    public void One_phone_on_two_voting_accounts_is_a_duplicate()
    {
        var a = Vote("Mələk", "Həsənova", work: 2, phone: "+994 50 999 88 77", signedUpMinutes: 200);
        var b = Vote("Mələk", "Həsənova", work: 2, phone: "0509998877", signedUpMinutes: 230);
        var all = VoteSuspicion.Score([a, b], Opened);
        Assert.Contains(Of(all, b).Reasons, r => r.StartsWith("Telefonu səs verən başqa 1 hesabla eynidir"));
        Assert.True(Of(all, b).Score >= 99 - 10);
    }

    [Fact]
    public void Many_new_accounts_of_one_work_from_one_address_add_up_and_the_score_is_capped()
    {
        var crowd = Enumerable.Range(0, 10)
            .Select(i => Vote($"Ad{(char)('a' + i)}", $"Soyad{(char)('a' + i)}", work: 3, ips: ["5.5.5.5"],
                signedUpMinutes: 300 + i * 2, votedMinutes: 300.5 + i * 2))
            .ToList();
        var all = VoteSuspicion.Score(crowd, Opened);
        Assert.All(all, s =>
        {
            Assert.Contains(s.Reasons, r => r.StartsWith("Bu işə eyni IP-dən daha 9 hesab"));
            Assert.Contains(s.Reasons, r => r == "Hesab açılan kimi (2 dəqiqə içində) səs verib");
            Assert.InRange(s.Score, VoteSuspicion.High, 99);
        });
    }

    /// <summary>
    /// D12's line between evidence and suspicion, as the owner narrowed it: one phone on three or more voting
    /// accounts — and nothing else. A repeated name and the author's phone still score; they are not evidence.
    /// </summary>
    [Fact]
    public void Only_a_phone_cluster_is_evidence_and_every_other_sign_is_review_only()
    {
        // Twenty accounts from one address, voting within seconds of signing up: a lot of suspicion, no evidence.
        var crowd = Enumerable.Range(0, 20)
            .Select(i => Vote($"Ad{(char)('a' + i)}", $"Soyad{(char)('a' + i)}", work: 4, ips: ["9.9.9.9"],
                signedUpMinutes: 400 + i, votedMinutes: 400.2 + i))
            .ToList();
        Assert.All(VoteSuspicion.Score(crowd, Opened), s =>
        {
            Assert.True(s.Score >= VoteSuspicion.High);
            Assert.Equal(VoteEvidence.Weak, s.Evidence);
        });

        // One phone on two accounts: suspicious; on three: a phone cluster, for each of the three.
        var two = new[] { "Elvin", "Rauf" }.Select(n => Vote(n, "Əliyev", work: 5, phone: "+994 70 555 44 33")).ToList();
        Assert.All(VoteSuspicion.Score(two, Opened), s => Assert.Equal(VoteEvidence.Weak, s.Evidence));
        var three = new[] { "Elvin", "Rauf", "Samir" }.Select(n => Vote(n, "Əliyev", work: 5, phone: "070 555 44 33")).ToList();
        Assert.All(VoteSuspicion.Score(three, Opened), s =>
        {
            Assert.Equal(VoteEvidence.PhoneCluster, s.Evidence);
            Assert.Equal(3, s.PhoneGroup);
        });

        // One name on three accounts opened during the vote: a strong sign, counted and shown — review only.
        var newNames = Enumerable.Range(0, 3).Select(_ => Vote("Səbinə", "Rzayeva", work: 6, signedUpMinutes: 50)).ToList();
        Assert.All(VoteSuspicion.Score(newNames, Opened), s =>
        {
            Assert.Equal(VoteEvidence.Weak, s.Evidence);
            Assert.Equal(3, s.NewNameGroup);
            Assert.Contains(s.Reasons, r => r.StartsWith("Eyni ad-soyadla"));
        });

        // The author's own phone on one voting account: the score's strongest sign, still review only.
        var self = Vote("Tofiq", "Vəliyev", phone: "+994 55 111 22 33");
        var scored = Assert.Single(VoteSuspicion.Score([self], Opened));
        Assert.Equal(VoteEvidence.Weak, scored.Evidence);
        Assert.True(scored.AuthorPhone);
        Assert.True(scored.Score >= 90);

        // All of them at once, without a phone cluster: still review only.
        var mixed = Enumerable.Range(0, 3)
            .Select(i => Vote("Səbinə", "Rzayeva", work: 7, phone: i == 0 ? "+994 55 111 22 33" : null, ips: ["7.7.7.7"],
                signedUpMinutes: 60 + i, votedMinutes: 60.1 + i, email: $"sebine{i}@example.com"))
            .ToList();
        Assert.All(VoteSuspicion.Score(mixed, Opened), s => Assert.Equal(VoteEvidence.Weak, s.Evidence));

        // And a genuine vote has no evidence at all.
        var plain = Vote("Kamran", "Babayev");
        Assert.Equal(VoteEvidence.None, Assert.Single(VoteSuspicion.Score([plain], Opened)).Evidence);
    }

    [Fact]
    public void Family_is_not_fraud()
    {
        var v = Vote("Xatirə", "Əliyev", signedUpMinutes: -6000);   // an old account, Roya Əliyeva's relative
        var s = Of(VoteSuspicion.Score([v], Opened), v);
        Assert.Equal(5, s.Score);
        Assert.Contains("ailə üzvü ola bilər", s.Reasons.Single());
    }
}
