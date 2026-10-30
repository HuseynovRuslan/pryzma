using Meydan.Api.Common;
using Meydan.Api.Features.Voting;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Meydan.Api.Features.Admin;

/// <summary>
/// The admin's review of a competition's votes (docs/voting/OWNER-DECISIONS.md, D12): what the suspicion score
/// sees, the evidence behind each work, and the one action it allows — setting chosen votes aside as fraudulent, or
/// restoring them — always by a person, always with a reason, always written to the audit log.
/// <para>
/// Nothing here decides on its own. There is no "set aside everything suspicious": the score only ranks what to
/// look at, and the server sets aside only votes of a phone cluster — a phone held by three or more voting accounts
/// of the competition (<see cref="VoteEvidence.PhoneCluster"/>) — and, once the owner switches R2 on, votes of a
/// coordinated identity (<see cref="VoteEvidence.CoordinatedIdentity"/>, <see cref="CoordinatedIdentityRule"/>).
/// Everything else, a repeated name and the author's phone included, is refused (<see cref="VoteEvidence.Weak"/>). A vote is set aside, never deleted, and a work is
/// never punished for the votes it received: it keeps its place on the ballot with its valid votes.
/// </para>
/// <para>
/// Phones, e-mails and IP addresses never leave the server: a repeated phone is "T1 (77 hesab)", a shared connection
/// "IP-2 (30 hesab)". The voter's name is shown for the votes under review, as the suspicion list already did.
/// </para>
/// </summary>
public sealed class VoteReview(
    MeydanDbContext db, AuditTrail audit, TimeProvider clock, IMemoryCache cache, IConfiguration config)
{
    private readonly CoordinatedIdentityRule _r2 = CoordinatedIdentityRule.From(config);
    private readonly DisposableEmailRule _d14 = DisposableEmailRule.From(config);

    /// <summary>A reason short enough to be a word is not a reason.</summary>
    public const int MinReasonLength = 10;
    public const int MaxReasonLength = 500;
    /// <summary>Far above any real review; a guard against a mistaken request, not a policy.</summary>
    public const int MaxVotesPerDecision = 500;

    private sealed record Row(
        long Id, int SubmissionId, string Reference, string Title, Guid UserId, Guid AuthorId,
        string VoterFirst, string VoterLast, string? VoterPhone, string? VoterEmail, DateTimeOffset SignedUpAt,
        DateTimeOffset VotedAt, string AuthorFirst, string AuthorLast, string? AuthorPhone,
        DateTimeOffset? InvalidatedAt, string? InvalidatedByFirst, string? InvalidatedByLast, string? InvalidationReason);

    private sealed record Reading(
        DateTimeOffset? VotingStartsAt, IReadOnlyList<Row> Rows, IReadOnlyDictionary<long, ScoredVote> Scored,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> Ips);

    /// <summary>Every vote of the competition — the set-aside ones too — scored together, as one population.</summary>
    private async Task<Reading> ReadAsync(int competitionId, CancellationToken ct)
    {
        var competition = await db.Competitions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == competitionId, ct)
                          ?? throw new NotFoundException("Müsabiqə tapılmadı.");
        var rows = await db.Votes.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Category.CompetitionId == competitionId)
            .OrderBy(v => v.Id)
            .Select(v => new Row(
                v.Id, v.SubmissionId, v.Submission.ReferenceNumber ?? "", v.Submission.Title, v.UserId, v.Submission.UserId,
                v.User.FirstName, v.User.LastName, v.User.PhoneNumber, v.User.Email, v.User.CreatedAt, v.CreatedAt,
                v.Submission.User.FirstName, v.Submission.User.LastName, v.Submission.User.PhoneNumber,
                v.InvalidatedAt, v.InvalidatedBy!.FirstName, v.InvalidatedBy!.LastName, v.InvalidationReason))
            .ToListAsync(ct);

        var people = rows.Select(r => r.UserId).Concat(rows.Select(r => r.AuthorId)).Distinct().ToList();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => people.Contains(t.UserId))
            .Select(t => new { t.UserId, Ip = t.CreatedByIp, t.CreatedAt })
            .ToListAsync(ct);
        var ips = tokens.Where(x => x.Ip != null)
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(x => x.Ip!).ToHashSet());
        // Registration signs the new account in, so its first token carries the address it was opened from. Tokens are
        // never deleted, only revoked or expired.
        var signUpIps = tokens.GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.MinBy(x => x.CreatedAt)!.Ip);

        var scored = VoteSuspicion.Score(
                rows.Select(r => new SuspicionVote(r.Id, r.SubmissionId, r.Reference, r.UserId, r.VoterFirst, r.VoterLast,
                    r.VoterPhone, r.VoterEmail, r.SignedUpAt, r.VotedAt, r.AuthorFirst, r.AuthorLast, r.AuthorPhone,
                    ips.GetValueOrDefault(r.UserId) ?? [], signUpIps.GetValueOrDefault(r.UserId))).ToList(),
                competition.VotingStartsAt, _r2)
            .ToDictionary(s => s.Vote.VoteId);
        // D14, worked out the same way everywhere (DisposableEmailRule.Meets). A phone cluster or R2 keeps its evidence;
        // the scores are untouched.
        foreach (var row in rows.Where(x => DisposableEmailRule.Meets(x.VoterEmail, x.SignedUpAt, competition.VotingStartsAt, competition.VotingEndsAt)))
        {
            var s = scored[row.Id];
            var sign = $"Müvəqqəti e-poçt xidməti ({DisposableEmailRule.ListedDomain(row.VoterEmail)}), hesab səsvermə müddətində açılıb (D14)";
            scored[row.Id] = _d14.Enabled && s.Evidence is not (VoteEvidence.PhoneCluster or VoteEvidence.CoordinatedIdentity)
                ? s with { Evidence = VoteEvidence.DisposableEmailDuringVoting, Reasons = [.. s.Reasons, sign] }
                : s with { Reasons = [.. s.Reasons, sign] };
        }
        return new Reading(competition.VotingStartsAt, rows, scored, ips);
    }

    // ---- The overview -----------------------------------------------------------------------------

    /// <summary>
    /// Per work and in total, over the votes that still count; plus, per work, how many were set aside and how
    /// many valid ones carry identity evidence. The list of suspicious votes holds valid votes scoring 30 or more.
    /// </summary>
    public async Task<AdminVoteSuspicionDto> OverviewAsync(int competitionId, CancellationToken ct)
    {
        var r = await ReadAsync(competitionId, ct);
        var valid = r.Rows.Where(x => x.InvalidatedAt is null).Select(x => r.Scored[x.Id]).ToList();
        var setAside = r.Rows.Where(x => x.InvalidatedAt is not null).ToLookup(x => x.SubmissionId);

        var works = r.Rows.GroupBy(x => x.SubmissionId)
            .Select(g =>
            {
                var counted = g.Where(x => x.InvalidatedAt is null).Select(x => r.Scored[x.Id]).ToList();
                var expected = Math.Round(counted.Sum(s => s.Score) / 100.0, 1);
                return new AdminSuspicionWorkDto(g.Key, g.First().Reference, g.First().Title, counted.Count,
                    counted.Count(s => s.Score >= VoteSuspicion.High),
                    counted.Count(s => s.Score is >= VoteSuspicion.Medium and < VoteSuspicion.High),
                    counted.Count(s => s.Score < VoteSuspicion.Medium),
                    expected, Math.Round(counted.Count - expected, 1),
                    g.Count(), setAside[g.Key].Count(), counted.Count(s => s.Evidence == VoteEvidence.PhoneCluster),
                    counted.Count(s => s.CoordinatedIdentity));
            })
            .OrderByDescending(w => w.Votes).ThenBy(w => w.Reference, StringComparer.Ordinal)
            .ToList();

        var suspicious = valid.Where(s => s.Score >= VoteSuspicion.Medium)
            .OrderByDescending(s => s.Score).ThenBy(s => s.Vote.VotedAt)
            .Select(s => new AdminSuspiciousVoteDto(s.Score, s.Vote.SubmissionId, s.Vote.Reference,
                $"{s.Vote.VoterFirst} {s.Vote.VoterLast}".Trim(), s.Vote.SignedUpAt, s.Vote.VotedAt, s.Reasons,
                s.Vote.VoteId, s.Evidence))
            .ToList();

        return new AdminVoteSuspicionDto(valid.Count,
            valid.Count(s => s.Score >= VoteSuspicion.High),
            valid.Count(s => s.Score is >= VoteSuspicion.Medium and < VoteSuspicion.High),
            valid.Count(s => s.Score < VoteSuspicion.Medium),
            works, suspicious, setAside.Sum(g => g.Count()));
    }

    // ---- One work, in full ------------------------------------------------------------------------

    public async Task<AdminWorkReviewDto> WorkAsync(int competitionId, int submissionId, CancellationToken ct)
    {
        var r = await ReadAsync(competitionId, ct);
        var mine = r.Rows.Where(x => x.SubmissionId == submissionId).ToList();
        if (mine.Count == 0)
        {
            var exists = await db.Submissions.AsNoTracking()
                .AnyAsync(s => s.Id == submissionId && s.CompetitionId == competitionId, ct);
            if (!exists) throw new NotFoundException("İş tapılmadı.");
        }

        // Labels instead of values. Phone and name groups are read across the whole competition — the same phone
        // voting for two works is one group — and numbered by size; connections are read within this work.
        var phoneLabels = Labels(r.Rows, x => VoteSuspicion.Phone(x.VoterPhone), "T");
        var nameLabels = Labels(r.Rows, x => $"{VoteSuspicion.Norm(x.VoterFirst)} {VoteSuspicion.Norm(x.VoterLast)}", "A");
        var ipGroups = mine
            .SelectMany(x => (r.Ips.GetValueOrDefault(x.UserId) ?? []).Select(ip => (ip, x.UserId)))
            .GroupBy(p => p.ip)
            .Select(g => (Ip: g.Key, Voters: g.Select(p => p.UserId).ToHashSet()))
            .Where(g => g.Voters.Count >= 3)
            .OrderByDescending(g => g.Voters.Count).ThenBy(g => g.Ip, StringComparer.Ordinal)
            .Select((g, i) => (Label: $"IP-{i + 1}", g.Voters))
            .ToList();

        var authorIps = mine.Count > 0 ? r.Ips.GetValueOrDefault(mine[0].AuthorId) ?? [] : [];
        bool IsNew(Row x) => r.VotingStartsAt is { } s && x.SignedUpAt >= s;
        var fresh = mine.Where(IsNew).OrderBy(x => x.VotedAt).ToList();
        var burst = fresh.Count == 0 ? 0
            : fresh.Max(a => fresh.Count(b => b.VotedAt >= a.VotedAt && b.VotedAt <= a.VotedAt.AddMinutes(15)));
        var gaps = mine.Select(x => (x.VotedAt - x.SignedUpAt).TotalSeconds).Where(s => s >= 0).Order().ToList();

        var phoneGroups = mine.Select(x => phoneLabels.GetValueOrDefault(x.Id)).Where(l => l is not null).ToList();
        var nameGroups = mine.Select(x => nameLabels.GetValueOrDefault(x.Id)).Where(l => l is not null).ToList();
        var title = mine.FirstOrDefault()?.Title
                    ?? await db.Submissions.AsNoTracking().Where(s => s.Id == submissionId).Select(s => s.Title).FirstAsync(ct);
        var reference = mine.FirstOrDefault()?.Reference
                        ?? await db.Submissions.AsNoTracking().Where(s => s.Id == submissionId).Select(s => s.ReferenceNumber ?? "").FirstAsync(ct);

        var evidence = new AdminWorkEvidenceDto(
            submissionId, reference, title,
            RawVotes: mine.Count,
            ValidVotes: mine.Count(x => x.InvalidatedAt is null),
            InvalidatedVotes: mine.Count(x => x.InvalidatedAt is not null),
            PhoneClusterVotes: mine.Count(x => x.InvalidatedAt is null && r.Scored[x.Id].Evidence == VoteEvidence.PhoneCluster),
            RepeatedPhoneVotes: phoneGroups.Count,
            LargestPhoneGroup: phoneGroups.Count == 0 ? 0 : phoneGroups.Max(l => l!.Value.Size),
            RepeatedNameVotes: nameGroups.Count,
            LargestNameGroup: nameGroups.Count == 0 ? 0 : nameGroups.Max(l => l!.Value.Size),
            LargestIpGroup: ipGroups.Count == 0 ? 0 : ipGroups[0].Voters.Count,
            AuthorIpVotes: mine.Count(x => (r.Ips.GetValueOrDefault(x.UserId) ?? []).Any(authorIps.Contains)),
            AuthorPhoneVotes: mine.Count(x => r.Scored[x.Id].AuthorPhone),
            NewAccountVotes: fresh.Count,
            MedianSecondsToVote: gaps.Count == 0 ? null : (int)gaps[gaps.Count / 2],
            LargestBurst15Min: burst,
            FirstSignUp: mine.Count == 0 ? null : mine.Min(x => x.SignedUpAt),
            LastSignUp: mine.Count == 0 ? null : mine.Max(x => x.SignedUpAt),
            FirstVote: mine.Count == 0 ? null : mine.Min(x => x.VotedAt),
            LastVote: mine.Count == 0 ? null : mine.Max(x => x.VotedAt),
            CoordinatedIdentityVotes: mine.Count(x => x.InvalidatedAt is null && r.Scored[x.Id].CoordinatedIdentity),
            CoordinatedIdentityEnabled: _r2.Enabled);

        var votes = mine
            .Select(x =>
            {
                var s = r.Scored[x.Id];
                var phone = phoneLabels.GetValueOrDefault(x.Id);
                var name = nameLabels.GetValueOrDefault(x.Id);
                var ip = ipGroups.FirstOrDefault(g => g.Voters.Contains(x.UserId));
                return new AdminReviewVoteDto(
                    x.Id, $"{x.VoterFirst} {x.VoterLast}".Trim(), x.SignedUpAt, x.VotedAt, s.Score, s.Evidence, s.Reasons,
                    phone?.Label, phone?.Size ?? 0, name?.Label, name?.Size ?? 0, ip.Label, ip.Voters?.Count ?? 0,
                    x.InvalidatedAt, x.InvalidatedAt is null ? null : $"{x.InvalidatedByFirst} {x.InvalidatedByLast}".Trim(),
                    x.InvalidationReason, s.CoordinatedIdentity);
            })
            .OrderByDescending(v => v.Evidence).ThenByDescending(v => v.Score).ThenBy(v => v.VotedAt)
            .ToList();
        return new AdminWorkReviewDto(evidence, votes);
    }

    /// <summary>Groups of two or more votes sharing a key, labelled T1, T2… by size: (label, size) per vote id.</summary>
    private static Dictionary<long, (string Label, int Size)?> Labels(IReadOnlyList<Row> rows, Func<Row, string> key, string prefix)
    {
        var result = new Dictionary<long, (string, int)?>();
        var groups = rows.Where(x => key(x).Trim().Length > 0)
            .GroupBy(key)
            .Select(g => (Rows: g.ToList(), Accounts: g.Select(x => x.UserId).Distinct().Count()))
            .Where(g => g.Accounts >= 2)
            .OrderByDescending(g => g.Accounts).ThenBy(g => g.Rows.Min(x => x.Id))
            .ToList();
        for (var i = 0; i < groups.Count; i++)
            foreach (var x in groups[i].Rows)
                result[x.Id] = ($"{prefix}{i + 1}", groups[i].Accounts);
        return result;
    }

    // ---- Decisions --------------------------------------------------------------------------------

    /// <summary>
    /// Sets the chosen votes aside, all of them or none: every id must be a valid vote of this competition whose voter
    /// belongs to a phone cluster (or, with R2 switched on, a coordinated identity), and the reason must say why. One audit entry per vote, in the same transaction.
    /// </summary>
    public Task<AdminVoteDecisionDto> InvalidateAsync(int competitionId, VoteDecisionRequest req, Guid adminId, CancellationToken ct) =>
        DecideAsync(competitionId, req, adminId, invalidate: true, ct);

    /// <summary>Restores votes an admin set aside — the same checks the other way round, and its own audit entry.</summary>
    public Task<AdminVoteDecisionDto> RestoreAsync(int competitionId, VoteDecisionRequest req, Guid adminId, CancellationToken ct) =>
        DecideAsync(competitionId, req, adminId, invalidate: false, ct);

    private async Task<AdminVoteDecisionDto> DecideAsync(
        int competitionId, VoteDecisionRequest req, Guid adminId, bool invalidate, CancellationToken ct)
    {
        var reason = (req.Reason ?? "").Trim();
        var ids = (req.VoteIds ?? []).Distinct().ToList();
        var errors = new Dictionary<string, string[]>();
        if (reason.Length < MinReasonLength)
            errors["reason"] = [$"Səbəbi yazın (ən azı {MinReasonLength} simvol)."];
        else if (reason.Length > MaxReasonLength)
            errors["reason"] = [$"Səbəb {MaxReasonLength} simvoldan uzun olmamalıdır."];
        if (ids.Count == 0)
            errors["voteIds"] = ["Ən azı bir səs seçin."];
        else if (ids.Count > MaxVotesPerDecision)
            errors["voteIds"] = [$"Bir dəfəyə ən çox {MaxVotesPerDecision} səs seçmək olar."];
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var r = await ReadAsync(competitionId, ct);
        var chosen = r.Rows.Where(x => ids.Contains(x.Id)).ToList();
        if (chosen.Count != ids.Count)
            throw new NotFoundException("Seçilmiş səslərdən bəziləri bu müsabiqədə tapılmadı. Heç nə dəyişmədi.");

        if (invalidate)
        {
            var already = chosen.Count(x => x.InvalidatedAt is not null);
            if (already > 0)
                throw new ConflictException($"{already} səs artıq etibarsız sayılıb. Siyahını yeniləyin; heç nə dəyişmədi.");
            // D12: only a phone held by three or more voting accounts — and R2 once the owner switches it on. A repeated
            // name, the author's phone, a shared connection, timing or a look-alike e-mail is never enough, alone or together.
            var outside = chosen.Count(x => r.Scored[x.Id].Evidence is not
                (VoteEvidence.PhoneCluster or VoteEvidence.CoordinatedIdentity or VoteEvidence.DisposableEmailDuringVoting));
            if (outside > 0)
                throw new UnprocessableException(_r2.Enabled
                    ? $"{outside} səs nə telefon klasterinə, nə də R2 koordinasiyalı kimlik qaydasına aid deyil. Təkrar ad, " +
                      "müəllifin telefonu, IP, vaxt və oxşar e-mail tək-tək yalnız yoxlama əlamətidir; belə səs etibarsız sayıla " +
                      "bilməz. Heç nə dəyişmədi."
                    : $"{outside} səs telefon klasterinə aid deyil: səsverənin telefonu müsabiqədə {VoteSuspicion.PhoneClusterSize} " +
                      "və ya daha çox səsverən hesabda istifadə olunmayıb. Təkrar ad, müəllifin telefonu, IP, vaxt və oxşar e-mail " +
                      "yalnız yoxlama əlamətidir; belə səs etibarsız sayıla bilməz. Heç nə dəyişmədi.");
        }
        else
        {
            var counted = chosen.Count(x => x.InvalidatedAt is null);
            if (counted > 0)
                throw new ConflictException($"{counted} səs onsuz da etibarlıdır. Siyahını yeniləyin; heç nə dəyişmədi.");
        }

        var now = clock.GetUtcNow();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // The condition is part of the write, so a second admin acting at the same moment changes nothing twice:
            // if any row has moved since it was read, the count is short and the whole decision is rolled back.
            var target = db.Votes.IgnoreQueryFilters()
                .Where(v => ids.Contains(v.Id) && v.Category.CompetitionId == competitionId);
            var changed = invalidate
                ? await target.Where(v => v.InvalidatedAt == null).ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.InvalidatedAt, now)
                    .SetProperty(v => v.InvalidatedById, adminId)
                    .SetProperty(v => v.InvalidationReason, reason), ct)
                : await target.Where(v => v.InvalidatedAt != null).ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.InvalidatedAt, (DateTimeOffset?)null)
                    .SetProperty(v => v.InvalidatedById, (Guid?)null)
                    .SetProperty(v => v.InvalidationReason, (string?)null), ct);
            if (changed != ids.Count)
                throw new ConflictException("Səslər bu arada dəyişib. Siyahını yeniləyin; heç nə dəyişmədi.");

            foreach (var x in chosen)
                audit.Record(adminId, invalidate ? AuditActions.VoteInvalidated : AuditActions.VoteRestored,
                    AuditEntities.Vote, x.Id, $"{x.Reference}: {reason}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        // The public ranking reads valid votes only; its shared copy must not outlive the decision.
        PublicVoting.ForgetLeaderboard(cache, competitionId);
        return new AdminVoteDecisionDto(ids.Count, chosen.Select(x => x.Reference).Distinct().Order(StringComparer.Ordinal).ToList());
    }

    // ---- D14: disposable e-mail during the vote, competition-wide ---------------------------------------------------

    private sealed record RuleRow(
        long Id, int SubmissionId, string Reference, string? Email, DateTimeOffset AccountOpenedAt, DateTimeOffset VotedAt,
        DateTimeOffset? InvalidatedAt, string? InvalidationReason)
    {
        public bool Valid => InvalidatedAt is null;
        public bool UnderRule => InvalidationReason?.StartsWith(DisposableEmailRule.PolicyId, StringComparison.Ordinal) == true;
    }

    private async Task<(Competition Competition, List<RuleRow> Rows)> RuleRowsAsync(int competitionId, CancellationToken ct)
    {
        var competition = await db.Competitions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == competitionId, ct)
                          ?? throw new NotFoundException("Müsabiqə tapılmadı.");
        var rows = await db.Votes.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Category.CompetitionId == competitionId)
            .OrderBy(v => v.Id)
            .Select(v => new RuleRow(v.Id, v.SubmissionId, v.Submission.ReferenceNumber ?? "", v.User.Email, v.User.CreatedAt,
                v.CreatedAt, v.InvalidatedAt, v.InvalidationReason))
            .ToListAsync(ct);
        return (competition, rows);
    }

    private bool MeetsD14(Competition c, RuleRow x) =>
        DisposableEmailRule.Meets(x.Email, x.AccountOpenedAt, c.VotingStartsAt, c.VotingEndsAt);

    private AdminDisposableEmailDto RuleSummary(Competition c, List<RuleRow> rows)
    {
        var meeting = rows.Where(x => MeetsD14(c, x)).ToList();
        var listedBefore = rows.Count(x => DisposableEmailRule.ListedDomain(x.Email) is not null
                                           && c.VotingStartsAt is { } s && x.AccountOpenedAt < s);
        var works = meeting.Select(x => x.SubmissionId).Distinct()
            .Select(id =>
            {
                var all = rows.Where(x => x.SubmissionId == id).ToList();
                var eligible = meeting.Count(x => x.SubmissionId == id && x.Valid);
                var validNow = all.Count(x => x.Valid);
                return new AdminRuleWorkDto(id, all[0].Reference, validNow, eligible, validNow - eligible,
                    meeting.Count(x => x.SubmissionId == id && !x.Valid && x.UnderRule));
            })
            .OrderByDescending(w => w.Eligible + w.SetAsideUnderRule).ThenBy(w => w.Reference, StringComparer.Ordinal)
            .ToList();
        var domains = DisposableEmailRule.Domains
            .Select(d =>
            {
                var mine = meeting.Where(x => DisposableEmailRule.ListedDomain(x.Email) == d.Key).ToList();
                return new AdminRuleDomainDto(d.Key, d.Value, mine.Count, mine.Count(x => x.Valid), mine.Count(x => !x.Valid && x.UnderRule));
            })
            .OrderByDescending(d => d.Votes).ThenBy(d => d.Domain, StringComparer.Ordinal)
            .ToList();
        var evidence = meeting
            .Select(x =>
            {
                var domain = DisposableEmailRule.ListedDomain(x.Email)!;
                return new AdminRuleEvidenceDto(x.Id, x.Reference, DisposableEmailRule.Mask(x.Email), domain,
                    DisposableEmailRule.Domains[domain], x.AccountOpenedAt, x.VotedAt, x.Valid, !x.Valid && x.UnderRule);
            })
            .ToList();
        return new AdminDisposableEmailDto(
            DisposableEmailRule.PolicyId, DisposableEmailRule.Version, DisposableEmailRule.EvidenceType, _d14.Enabled,
            DisposableEmailRule.Reason, DisposableEmailRule.Source, DisposableEmailRule.Domains.Count,
            c.VotingStartsAt, c.VotingEndsAt, c.VotingStateAt(clock.GetUtcNow()) == VotingState.Closed,
            meeting.Count(x => x.Valid), meeting.Count(x => !x.Valid && x.UnderRule), meeting.Count(x => !x.Valid && !x.UnderRule),
            listedBefore, works, domains, evidence);
    }

    public async Task<AdminDisposableEmailDto> DisposableEmailAsync(int competitionId, CancellationToken ct)
    {
        var (c, rows) = await RuleRowsAsync(competitionId, ct);
        return RuleSummary(c, rows);
    }

    /// <summary>
    /// Sets aside, in one decision, every valid vote of the competition that meets D14 — whichever work it went to.
    /// Refused while the rule is off, while the vote is open, when nothing meets it, or when the count is not the one
    /// the admin was shown (the votes moved meanwhile). Never deletes; one audit entry per vote with its evidence
    /// domain, and one for the decision.
    /// </summary>
    public async Task<AdminDisposableEmailDto> ApplyDisposableEmailAsync(
        int competitionId, ApplyVotingRuleRequest req, Guid adminId, CancellationToken ct)
    {
        if (!_d14.Enabled)
            throw new ConflictException("Müvəqqəti e-poçt qaydası (D14) aktiv deyil. Heç nə dəyişmədi.");
        var reason = string.IsNullOrWhiteSpace(req.Reason) ? DisposableEmailRule.Reason : req.Reason.Trim();
        var stored = $"{DisposableEmailRule.PolicyId} v{DisposableEmailRule.Version}: {reason}";
        if (reason.Length < MinReasonLength || stored.Length > MaxReasonLength)
            throw ValidationFailedException.For("reason", $"Səbəb {MinReasonLength}–{MaxReasonLength - 30} simvol olmalıdır.");

        var (c, rows) = await RuleRowsAsync(competitionId, ct);
        if (c.VotingStateAt(clock.GetUtcNow()) != VotingState.Closed)
            throw new ConflictException("Qayda yalnız səsvermə bağlandıqdan sonra tətbiq olunur. Heç nə dəyişmədi.");
        var eligible = rows.Where(x => x.Valid && MeetsD14(c, x)).ToList();
        if (eligible.Count == 0)
            throw new ConflictException("Qaydaya uyğun etibarlı səs yoxdur. Heç nə dəyişmədi.");
        if (req.ExpectedVotes != eligible.Count)
            throw new ConflictException(
                $"Gözlənilən {req.ExpectedVotes} səs idi, qaydaya uyğun etibarlı səs isə {eligible.Count}. Heç nə dəyişmədi.");

        var ids = eligible.Select(x => x.Id).ToList();
        var perWork = string.Join(", ", eligible.GroupBy(x => x.Reference).OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));
        var now = clock.GetUtcNow();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // As in DecideAsync: the condition is part of the write, all or none.
            var changed = await db.Votes.IgnoreQueryFilters()
                .Where(v => ids.Contains(v.Id) && v.Category.CompetitionId == competitionId && v.InvalidatedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.InvalidatedAt, now)
                    .SetProperty(v => v.InvalidatedById, adminId)
                    .SetProperty(v => v.InvalidationReason, stored), ct);
            if (changed != ids.Count)
                throw new ConflictException("Səslər bu arada dəyişib. Heç nə dəyişmədi.");
            foreach (var x in eligible)
                audit.Record(adminId, AuditActions.VoteInvalidated, AuditEntities.Vote, x.Id,
                    $"{x.Reference}: {stored} [evidence {DisposableEmailRule.EvidenceType}; domain {DisposableEmailRule.ListedDomain(x.Email)}]");
            audit.Record(adminId, AuditActions.VotingRuleApplied, AuditEntities.Competition, competitionId,
                $"{DisposableEmailRule.PolicyId} v{DisposableEmailRule.Version} ({DisposableEmailRule.EvidenceType}): {ids.Count} votes set aside " +
                $"({perWork}); {DisposableEmailRule.Domains.Count} domains; window {c.VotingStartsAt:O} – {c.VotingEndsAt:O}; reason: {reason}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        PublicVoting.ForgetLeaderboard(cache, competitionId);
        var (c2, after) = await RuleRowsAsync(competitionId, ct);
        return RuleSummary(c2, after);
    }
}
