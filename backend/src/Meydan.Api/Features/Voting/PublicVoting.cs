using Meydan.Api.Common;
using Meydan.Api.Features.Admin;
using Meydan.Api.Features.Catalog;
using Meydan.Api.Features.Results;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Meydan.Api.Features.Voting;

/// <summary>
/// The public vote: reading the ballot and casting on it. Every method goes through <see cref="GateAsync"/>, which
/// is to voting what <see cref="PublicResults"/>'s gate is to results — until an admin sets a voting window on a
/// published competition of a published project, none of this exists as far as the public API is concerned, and
/// the same 404 comes back whether the competition is unknown, a draft, or simply has no vote.
/// <para>
/// What it may show is fixed by docs/voting/OWNER-DECISIONS.md D9: the reference, the nomination, the
/// server-made safe picture, the count, and the title and answers of fields an admin has marked public — the
/// same per-field switch that decides what the gallery shows, so the ballot can never expose a field the gallery
/// would not. Never a name, an e-mail, an original file or an admin note. The owner's id is read for one purpose
/// only — comparing it with the viewer's, so that nobody votes for their own work — and never leaves this class.
/// </para>
/// <para>
/// Which files are public-safe is decided exactly as the gallery decides it: a file of a field an admin marked
/// public after results, whose server-made derivative is Ready. The two surfaces are held together by a test that
/// compares them rather than by a shared method, because the rule is a privacy rule and a helper quietly changed
/// is worth less than a test that fails.
/// </para>
/// </summary>
public sealed class PublicVoting(
    MeydanDbContext db, IFileStorage storage, AuditTrail audit, TimeProvider clock, DocumentPages pages,
    IMemoryCache cache, IConfiguration config, Auth.EmailConfirmationPolicy confirmation)
{
    private const string NoVote = "Bu müsabiqədə səsvermə yoxdur.";
    private const string NotOpenYet = "Səsvermə hələ başlamayıb.";
    private const string NotOnTheBallot = "Bu iş səsverməyə çıxarılmayıb.";
    private const string Ended = "Səsvermə başa çatıb. Səsiniz qəbul edilmədi.";

    private sealed record PublicField(int Id, string Label, CompetitionFieldType Type, bool IsTitle);

    private sealed record Gate(Competition Competition, VotingState State, IReadOnlyList<PublicField> Fields)
    {
        public bool TitleIsPublic => Fields.Any(f => f.IsTitle);

        public IReadOnlySet<int> PublicFileFieldIds =>
            Fields.Where(f => f.Type == CompetitionFieldType.File).Select(f => f.Id).ToHashSet();

        /// <summary>
        /// The field a card describes a work with: the first public long-text answer that is not the title,
        /// in the admin's own field order. On the settlement competitions that is "Konseptin izahı"; the
        /// symbols field and the private note are long text too, but neither is public, so neither is ever
        /// picked. A competition whose long-text fields are all private has no description here at all.
        /// </summary>
        public int? ConceptFieldId =>
            Fields.FirstOrDefault(f => f.Type == CompetitionFieldType.LongText && !f.IsTitle)?.Id;
    }

    private sealed record EntryFile(int Id, int? FieldId, string ContentType, PublicAssetStatus Asset, int? Width, int? Height, bool Hidden);

    /// <summary>A work on the ballot. <c>OwnerId</c> is compared, never published — see the class remarks.</summary>
    private sealed record Entry(
        int Id, string Reference, string Title, Guid OwnerId,
        int CategoryId, string CategorySlug, string CategoryName,
        DateTimeOffset? SubmittedAt, int? PreviewFileId, IReadOnlyList<EntryFile> Files);

    /// <summary>What the signed-in viewer has already done here, read once per request.</summary>
    /// <param name="NeedsEmail">Must confirm the e-mail address before voting (<see cref="Auth.EmailConfirmationPolicy"/>).</param>
    private sealed record Voter(Guid UserId, IReadOnlyDictionary<int, int> VotesByCategory, bool NeedsEmail = false);

    private sealed record Nomination(int Id, string Slug, string Name);

    // ---- Reading ----------------------------------------------------------------------------------

    public async Task<VotingDto> SummaryAsync(string slug, Guid? userId, CancellationToken ct)
    {
        var gate = await GateAsync(slug, ct);
        var voter = await VoterAsync(gate, userId, ct);
        var nominations = await NominationsAsync(gate.Competition.Id, ct);

        var works = (await Voteable(gate.Competition.Id)
                .GroupBy(s => s.CompetitionCategoryId!.Value)
                .Select(g => new { CategoryId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.CategoryId, x => x.Count);
        var votes = await VotesByCategoryAsync(gate.Competition.Id, ct);
        var cast = db.Votes.AsNoTracking().Where(v => v.Category.CompetitionId == gate.Competition.Id);
        var voters = await cast.Select(v => v.UserId).Distinct().CountAsync(ct);
        var last = await cast.MaxAsync(v => (DateTimeOffset?)v.CreatedAt, ct);

        var rows = nominations
            .Select(n => new VotingNominationDto(n.Slug, n.Name,
                works.GetValueOrDefault(n.Id), votes.GetValueOrDefault(n.Id)))
            .ToList();

        // Published means what the results pages mean by it: an admin published the result and the competition no
        // longer takes entries. A result row that is still being prepared is not a result.
        var now = clock.GetUtcNow();
        var publishedAt = await db.CompetitionResults.AsNoTracking()
            .Where(r => r.CompetitionId == gate.Competition.Id)
            .Select(r => r.PublishedAt)
            .FirstOrDefaultAsync(ct);

        return new VotingDto(
            CompetitionDto(gate.Competition), gate.State,
            gate.Competition.VotingStartsAt, gate.Competition.VotingEndsAt,
            rows, rows.Sum(r => r.WorkCount), rows.Sum(r => r.VoteCount),
            await ViewerAsync(gate, voter, nominations, ct), voters, last,
            CompetitionPhases.AreResultsPublic(gate.Competition, publishedAt, now), now);
    }

    public async Task<VotingWorksDto> WorksAsync(
        string slug, string? nomination, string? q, int page, int pageSize, int? seed, Guid? userId, CancellationToken ct)
    {
        var gate = await OpenedGateAsync(slug, ct);
        var voter = await VoterAsync(gate, userId, ct);
        var nominations = await NominationsAsync(gate.Competition.Id, ct);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 60);

        var query = Voteable(gate.Competition.Id);
        if (!string.IsNullOrWhiteSpace(nomination))
        {
            // An unknown nomination is an empty page, not a 404: a stale filter in a shared link should not break it.
            var chosen = nominations.FirstOrDefault(n => n.Slug == nomination);
            query = chosen is null ? query.Where(_ => false) : query.Where(s => s.CompetitionCategoryId == chosen.Id);
        }

        // Search covers the title, and only where the title is public (D2): otherwise a searcher could confirm the
        // wording of a title nobody is allowed to read.
        if (!string.IsNullOrWhiteSpace(q) && gate.TitleIsPublic)
        {
            var pattern = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(s => EF.Functions.ILike(s.Title, pattern));
        }

        int total;
        List<Entry> entries;
        if (seed is { } shuffle)
        {
            // The visitor's own order. Sorting by submission time puts the first-submitted works on the first screen
            // for everybody, and sorting by votes would hand the most visible place to whoever is already ahead while
            // the count is public; a shuffle gives every work its turn at the top. Each work's place comes from the
            // seed and its own id alone, so the order is the same on every page, after a reload and inside a search,
            // and nothing is shown twice or skipped while paging. Nominations keep their order around it.
            var keys = await query
                .Select(s => new { s.Id, s.Category!.SortOrder, s.Category.Name })
                .ToListAsync(ct);
            total = keys.Count;
            var pageIds = keys
                .OrderBy(k => k.SortOrder).ThenBy(k => k.Name, StringComparer.Ordinal)
                .ThenBy(k => ShuffleKey(shuffle, k.Id)).ThenBy(k => k.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(k => k.Id)
                .ToList();
            var read = await ReadAsync(query.Where(s => pageIds.Contains(s.Id)), ct);
            entries = read.OrderBy(e => pageIds.IndexOf(e.Id)).ToList();
        }
        else
        {
            total = await query.CountAsync(ct);
            entries = await ReadAsync(
                query.OrderBy(s => s.Category!.SortOrder).ThenBy(s => s.Category!.Name)
                    .ThenBy(s => s.SubmittedAt).ThenBy(s => s.Id)
                    .Skip((page - 1) * pageSize).Take(pageSize),
                ct);
        }

        var ids = entries.Select(e => e.Id).ToList();
        var counts = await VoteCountsAsync(ids, ct);
        var concepts = await ConceptsAsync(gate, ids, ct);
        return new VotingWorksDto(gate.State,
            entries
                .Select(e => Card(gate, e, counts.GetValueOrDefault(e.Id), voter, concepts.GetValueOrDefault(e.Id)))
                .ToList(),
            total, page, pageSize, await ViewerAsync(gate, voter, nominations, ct));
    }

    /// <summary>
    /// The live ranking of every work on the ballot — the same works, by the same rule (<see cref="Voteable"/>),
    /// as the ballot itself, in one list whatever their category, since a person votes once per competition.
    /// <para>
    /// Order: votes, most first; inside a tie, the submission's id. The id says nothing about the work and never
    /// changes, so tied works keep their order between two reads instead of swapping places on the screen — and
    /// they share their rank, so the order inside a tie is not presented as a lead.
    /// </para>
    /// <para>
    /// Every open page asks for this every ten seconds, so the list itself — the same for everybody — is kept for
    /// a few seconds and shared; only the viewer's own vote is read per request. A vote cast here drops the copy at
    /// once, so the voter's next read already counts it. Two queries build the list (the works with their file
    /// metadata, and one grouped count), never one per work, and no answer or original file is read.
    /// </para>
    /// </summary>
    public async Task<VotingLeaderboardDto> LeaderboardAsync(string slug, Guid? userId, CancellationToken ct)
    {
        var gate = await OpenedGateAsync(slug, ct);
        var seconds = config.GetValue("Voting:LeaderboardCacheSeconds", 5);
        var ranking = seconds <= 0
            ? await RankAsync(gate, ct)
            : await cache.GetOrCreateAsync(LeaderboardKey(gate.Competition.Id), entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(seconds);
                entry.Size = 64 * 1024;
                return RankAsync(gate, ct);
            }) ?? await RankAsync(gate, ct);

        // The viewer's own vote, set aside or not: it is theirs, and it is spent either way.
        var mine = userId is { } id
            ? await db.Votes.IgnoreQueryFilters().AsNoTracking()
                .Where(v => v.UserId == id && v.Category.CompetitionId == gate.Competition.Id)
                .Select(v => v.Submission.ReferenceNumber)
                .FirstOrDefaultAsync(ct)
            : null;
        return ranking with { State = gate.State, MyVoteReference = mine };
    }

    private static string LeaderboardKey(int competitionId) => $"voting-leaderboard:{competitionId}";
    private static string AuditKey(int competitionId) => $"voting-audit:{competitionId}";

    /// <summary>Drops the shared ranking and the public audit of a competition, so their next read counts again —
    /// after a vote, or after an admin sets votes aside or restores them.</summary>
    internal static void ForgetLeaderboard(IMemoryCache cache, int competitionId)
    {
        cache.Remove(LeaderboardKey(competitionId));
        cache.Remove(AuditKey(competitionId));
    }

    /// <summary>
    /// The public audit: valid, set-aside and total votes, and one line per admin decision — its time, its number of
    /// votes and the rule it was taken under, in public words. Shared for a few seconds like the ranking, and dropped
    /// with it the moment a decision is taken.
    /// </summary>
    public async Task<VotingAuditDto> AuditAsync(string slug, CancellationToken ct)
    {
        var gate = await OpenedGateAsync(slug, ct);
        var seconds = config.GetValue("Voting:LeaderboardCacheSeconds", 5);
        var audit = seconds <= 0
            ? await BuildAuditAsync(gate, ct)
            : await cache.GetOrCreateAsync(AuditKey(gate.Competition.Id), entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(seconds);
                entry.Size = 16 * 1024;
                return BuildAuditAsync(gate, ct);
            }) ?? await BuildAuditAsync(gate, ct);
        return audit with { State = gate.State };
    }

    private async Task<VotingAuditDto> BuildAuditAsync(Gate gate, CancellationToken ct)
    {
        // Every vote of the competition, set aside or not. The phone is read only to tell which rule a decision was
        // taken under, here on the server; it never leaves this method.
        var votes = await db.Votes.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Category.CompetitionId == gate.Competition.Id)
            .Select(v => new { v.UserId, v.InvalidatedAt, Phone = v.User.PhoneNumber, v.InvalidationReason })
            .ToListAsync(ct);

        // An admin decision sets its votes aside at one instant, so the instant is the decision. The admin's own
        // reason is free text and stays private; the public line names the rule (VoteAuditRules — the same rule the
        // administration's vote analytics uses).
        var ruleOf = VoteAuditRules.Classifier(votes.Select(v => (v.UserId, v.Phone)));
        var setAside = votes.Where(v => v.InvalidatedAt is not null)
            .Select(v => (At: v.InvalidatedAt!.Value, Reason: ruleOf(v.InvalidationReason, v.Phone)))
            .ToList();
        var entries = setAside
            .GroupBy(v => v)
            .Select(g => new VotingAuditEntryDto(g.Key.At, g.Count(), g.Key.Reason))
            .OrderByDescending(e => e.At).ThenBy(e => e.Reason)
            .ToList();
        var rules = setAside.GroupBy(v => v.Reason).OrderBy(g => g.Key)
            .Select(g => new VotingAuditRuleDto(g.Key, g.Count()))
            .ToList();

        var invalidated = setAside.Count;
        return new VotingAuditDto(gate.State, votes.Count - invalidated, invalidated, votes.Count, entries, clock.GetUtcNow(), rules);
    }

    private async Task<VotingLeaderboardDto> RankAsync(Gate gate, CancellationToken ct)
    {
        var read = clock.GetUtcNow();
        var entries = await ReadAsync(Voteable(gate.Competition.Id), ct);
        var counts = (await db.Votes.AsNoTracking()
                .Where(v => v.Category.CompetitionId == gate.Competition.Id)
                .GroupBy(v => v.SubmissionId)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.Count);

        var ordered = entries
            .Select(e => (Entry: e, Votes: counts.GetValueOrDefault(e.Id)))
            .OrderByDescending(x => x.Votes).ThenBy(x => x.Entry.Id)
            .ToList();
        var rows = new List<VotingLeaderboardEntryDto>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (e, votes) = ordered[i];
            // Standard competition ranking: a tie shares the higher rank, and the next rank skips past it.
            var rank = i > 0 && votes == ordered[i - 1].Votes ? rows[i - 1].Rank : i + 1;
            rows.Add(new VotingLeaderboardEntryDto(rank, e.Reference,
                gate.TitleIsPublic && !string.IsNullOrWhiteSpace(e.Title) ? e.Title : null,
                votes, Preview(gate, e).Image));
        }
        return new VotingLeaderboardDto(gate.State, read, rows.Sum(r => r.VoteCount), rows, null);
    }

    /// <summary>
    /// A work's place in one visitor's shuffle: SplitMix64 over the seed and the work's id. Deterministic across
    /// restarts and machines (unlike <see cref="HashCode"/>), and well mixed even for consecutive ids.
    /// </summary>
    internal static ulong ShuffleKey(int seed, int id)
    {
        var z = ((ulong)(uint)seed << 32 | (uint)id) + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public async Task<VotingWorkDetailDto> WorkAsync(string slug, string reference, Guid? userId, CancellationToken ct)
    {
        var gate = await OpenedGateAsync(slug, ct);
        var entry = await EntryAsync(gate, reference, ct);
        var voter = await VoterAsync(gate, userId, ct);
        var counts = await VoteCountsAsync([entry.Id], ct);
        var answers = await AnswersAsync(gate, entry, ct);
        var concepts = await ConceptsAsync(gate, [entry.Id], ct);

        // Reading order: the chosen picture, the other images, then each PDF — its first page and, after it, every
        // other page. The name keeps the upload position, never the participant's own file name.
        var files = PublicFiles(gate, entry);
        var originals = await OriginalKeysAsync(entry, files.Where(IsPdf), ct);
        var shown = new List<GalleryFileDto>();
        foreach (var f in files.OrderByDescending(f => f.Id == entry.PreviewFileId).ThenByDescending(IsImage))
        {
            var url = FileUrl(gate, entry, f.Id);
            var name = $"Fayl {files.IndexOf(f) + 1}";
            shown.Add(new GalleryFileDto(f.Id, IsImage(f) ? "image" : "document-preview", url, url + "/thumb", f.Width, f.Height, name));
            if (!originals.TryGetValue(f.Id, out var original)) continue;
            var count = await pages.CountAsync(original, ct);
            for (var p = 2; p <= count; p++)
                shown.Add(new GalleryFileDto(f.Id, "document-page", $"{url}/pages/{p}", $"{url}/pages/{p}/thumb", null, null,
                    $"{name}, {p}. səhifə", p));
        }

        return new VotingWorkDetailDto(
            Card(gate, entry, counts.GetValueOrDefault(entry.Id), voter, concepts.GetValueOrDefault(entry.Id)),
            answers, shown,
            gate.State, gate.Competition.VotingEndsAt,
            await ViewerAsync(gate, voter, await NominationsAsync(gate.Competition.Id, ct), ct));
    }

    /// <summary>
    /// The answers of public fields, labelled as the question was asked when it was answered. Only fields an
    /// admin marked public, never the title field (it is the work's name, shown as such) and never a file field.
    /// An empty or missing answer is left out rather than shown blank.
    /// </summary>
    private async Task<List<GalleryAnswerDto>> AnswersAsync(Gate gate, Entry e, CancellationToken ct)
    {
        var fields = gate.Fields.Where(f => f.Type != CompetitionFieldType.File && !f.IsTitle).ToList();
        if (fields.Count == 0) return [];
        var ids = fields.Select(f => f.Id).ToList();
        var answers = await db.SubmissionAnswers.AsNoTracking()
            .Where(a => a.SubmissionId == e.Id && ids.Contains(a.CompetitionFieldId) && a.Value != null && a.Value != "")
            .Select(a => new { a.CompetitionFieldId, a.FieldLabel, a.Value })
            .ToListAsync(ct);
        return fields
            .Select(f => (Field: f, Answer: answers.FirstOrDefault(a => a.CompetitionFieldId == f.Id)))
            .Where(x => x.Answer is not null)
            .Select(x => new GalleryAnswerDto(x.Answer!.FieldLabel ?? x.Field.Label, x.Field.Type, x.Answer.Value!))
            .ToList();
    }

    /// <summary>
    /// A public-safe copy of a work's file while the vote is on — never the original, and only for a file of a
    /// public field whose derivative is ready, on a work that is on the ballot. Re-checked on every request, so a
    /// URL stops working the moment the window, the nomination or the work's status changes.
    /// </summary>
    public async Task<(Stream Content, FileType Type, string DownloadName, string ETag)> OpenFileAsync(
        string slug, string reference, int fileId, bool thumbnail, CancellationToken ct)
    {
        var gate = await OpenedGateAsync(slug, ct);
        var entry = await EntryAsync(gate, reference, ct);
        var files = PublicFiles(gate, entry);
        var index = files.FindIndex(f => f.Id == fileId);
        if (index < 0) throw new NotFoundException("Fayl tapılmadı.");

        var stored = await db.SubmissionFiles.AsNoTracking()
            .Where(f => f.Id == fileId && f.SubmissionId == entry.Id && f.PublicAssetStatus == PublicAssetStatus.Ready)
            .Select(f => new { f.PublicAssetKey, f.ThumbnailKey, f.ThumbnailStatus })
            .FirstOrDefaultAsync(ct);
        var key = thumbnail && stored?.ThumbnailStatus == PublicAssetStatus.Ready ? stored.ThumbnailKey : stored?.PublicAssetKey;
        if (key is null || !StorageAreas.IsIn(key, StorageAreas.Derived))
            throw new NotFoundException("Fayl tapılmadı.");
        var type = FileTypes.ByExtension(Path.GetExtension(key)) ?? throw new NotFoundException("Fayl tapılmadı.");
        Stream content;
        try
        {
            content = storage.OpenRead(key);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            throw new NotFoundException("Fayl tapılmadı.");
        }
        return (content, type, $"{entry.Reference}-{index + 1}{type.CanonicalExtension}", PublicResults.ETagOf(key));
    }

    /// <summary>
    /// A page after the first of a PDF on the ballot, as a public-safe image — under exactly the gate of the file
    /// itself: the window, the work, a public field, a ready first page, not hidden by an admin. The first page is
    /// the file's own address; asking for page 1 or past the end is a 404, like any file that is not there.
    /// </summary>
    public async Task<(Stream Content, FileType Type, string DownloadName, string ETag)> OpenPageAsync(
        string slug, string reference, int fileId, int page, bool thumbnail, CancellationToken ct)
    {
        var gate = await OpenedGateAsync(slug, ct);
        var entry = await EntryAsync(gate, reference, ct);
        var files = PublicFiles(gate, entry);
        var index = files.FindIndex(f => f.Id == fileId && IsPdf(f));
        if (index < 0 || page < 2) throw new NotFoundException("Səhifə tapılmadı.");
        var originals = await OriginalKeysAsync(entry, [files[index]], ct);
        if (!originals.TryGetValue(fileId, out var original) || page > await pages.CountAsync(original, ct))
            throw new NotFoundException("Səhifə tapılmadı.");

        PublicImage image;
        try
        {
            image = await pages.PageAsync(original, page, thumbnail, ct);
        }
        catch (DerivativeException)
        {
            throw new NotFoundException("Səhifə tapılmadı.");
        }
        return (new MemoryStream(image.Bytes, writable: false), FileTypes.Webp,
            $"{entry.Reference}-{index + 1}-{page}{FileTypes.Webp.CanonicalExtension}",
            PublicResults.ETagOf($"{original}#{page}{(thumbnail ? "t" : "")}"));
    }

    // ---- Casting ----------------------------------------------------------------------------------

    /// <summary>
    /// Casts one vote. Every rule that can be checked is checked first, so the caller gets a reason rather than a
    /// bare conflict.
    /// <para>
    /// The rule that decides the prize is one vote per person per <em>competition</em> (the owner's rule, 4 October
    /// 2026): categories only sort the ballot, they are not separate votes. The database's unique index is per
    /// nomination, so the competition-wide rule is held here, inside one transaction that first takes a lock on
    /// (competition, voter). Between a bare check and an insert another request fits; with the lock, a second
    /// request — for any work, in any category — waits for the first to finish, then finds its vote and is refused.
    /// Exactly one row exists either way, and the per-nomination index still stands behind it.
    /// </para>
    /// </summary>
    public async Task<VoteReceiptDto> CastAsync(Guid userId, string slug, string reference, CancellationToken ct)
    {
        var gate = await OpenedGateAsync(slug, ct);
        if (gate.State != VotingState.Open)
            throw new UnprocessableException(Ended);

        var entry = await EntryAsync(gate, reference, ct);
        if (entry.OwnerId == userId)
            throw new ForbiddenException("Öz işinizə səs verə bilməzsiniz.");

        var account = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.CreatedAt, u.EmailConfirmed }).FirstOrDefaultAsync(ct)
            ?? throw new ForbiddenException("Səs vermək üçün hesabınıza daxil olun.");
        if (confirmation.Requires(account.CreatedAt, account.EmailConfirmed))
            throw new ForbiddenException(Auth.EmailConfirmationPolicy.NotConfirmed);

        var now = clock.GetUtcNow();
        var competitionId = gate.Competition.Id;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Held until the transaction ends: the check below and the insert are one step for this voter here.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({competitionId}, {VoterLockKey(userId)})", ct);
            // A vote an admin set aside still spends the voter's one vote: setting it aside never opens a second.
            if (await db.Votes.IgnoreQueryFilters().AnyAsync(v => v.UserId == userId && v.Category.CompetitionId == competitionId, ct))
                throw new ConflictException(AlreadyVoted);

            // The window again, at the instant of the write: the gate above was read before this transaction and
            // before any wait on the lock, so a request that arrived a moment before the end must not be counted
            // after it. The window is read afresh too, in case an admin moved it meanwhile. The vote carries this
            // instant, so no counted vote is ever stamped at or after the end.
            now = clock.GetUtcNow();
            var window = await db.Competitions.AsNoTracking().Where(c => c.Id == competitionId)
                .Select(c => new Competition { VotingStartsAt = c.VotingStartsAt, VotingEndsAt = c.VotingEndsAt })
                .SingleAsync(ct);
            switch (window.VotingStateAt(now))
            {
                case VotingState.Open: break;
                case VotingState.Upcoming: throw new UnprocessableException(NotOpenYet);
                case VotingState.NotConfigured: throw new UnprocessableException(NoVote);
                default: throw new UnprocessableException(Ended);
            }

            db.Votes.Add(new Vote
            {
                UserId = userId,
                SubmissionId = entry.Id,
                CompetitionCategoryId = entry.CategoryId,
                CreatedAt = now,
            });
            // The same unit of work as the vote itself: a vote without its record cannot exist, nor a record without
            // its vote. The summary names the category and nothing about the voter.
            audit.Record(userId, AuditActions.VoteCast, AuditEntities.Submission, entry.Id, entry.CategorySlug);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                throw new ConflictException(AlreadyVoted);
            }
            await tx.CommitAsync(ct);
        });
        // The shared ranking is now one vote behind; the next read builds it afresh.
        ForgetLeaderboard(cache, competitionId);

        // Read after the insert, never before: the number the voter is shown includes their own vote.
        var counts = await VoteCountsAsync([entry.Id], ct);
        return new VoteReceiptDto(entry.Reference, entry.CategorySlug, counts.GetValueOrDefault(entry.Id), now);
    }

    private const string AlreadyVoted = "Siz bu müsabiqədə artıq səs vermisiniz. Müsabiqədə yalnız bir işə səs vermək olar.";

    /// <summary>The voter half of the lock's key: any stable 32-bit digest of the id will do — a clash only queues two voters.</summary>
    private static int VoterLockKey(Guid userId)
    {
        var bytes = userId.ToByteArray();
        return BitConverter.ToInt32(bytes, 0) ^ BitConverter.ToInt32(bytes, 4) ^ BitConverter.ToInt32(bytes, 8) ^ BitConverter.ToInt32(bytes, 12);
    }

    // ---- The gate ---------------------------------------------------------------------------------

    /// <summary>The competition, if it is public at all and has a voting window.</summary>
    private async Task<Gate> GateAsync(string slug, CancellationToken ct)
    {
        var c = await db.Competitions.AsNoTracking()
            .Include(x => x.Project)
            .FirstOrDefaultAsync(x => x.Slug == slug && x.Project.Status == ProjectStatus.Published, ct);
        if (c is null || !Competition.IsPubliclyVisible(c.Status)) throw new NotFoundException(NoVote);
        var state = c.VotingStateAt(clock.GetUtcNow());
        if (state == VotingState.NotConfigured) throw new NotFoundException(NoVote);

        var fields = await db.CompetitionFields.AsNoTracking()
            .Where(f => f.CompetitionId == c.Id && f.IsPublicAfterResults)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
            .Select(f => new PublicField(f.Id, f.Label, f.FieldType, f.IsSubmissionTitle))
            .ToListAsync(ct);
        return new Gate(c, state, fields);
    }

    /// <summary>The same gate, and: the works themselves become public only once the window has opened.</summary>
    private async Task<Gate> OpenedGateAsync(string slug, CancellationToken ct)
    {
        var gate = await GateAsync(slug, ct);
        if (gate.State == VotingState.Upcoming) throw new NotFoundException(NotOpenYet);
        return gate;
    }

    /// <summary>
    /// Final entries that can win, placed in a nomination that is switched on. A draft, a rejected or a withdrawn
    /// entry is never on the ballot — the same statuses the gallery uses, read from the same constant so that a
    /// change in one place cannot leave the two disagreeing.
    /// </summary>
    private IQueryable<Submission> Voteable(int competitionId) => BallotWorks(db, competitionId);

    /// <summary>
    /// Every work on the ballot: a final status, a reference, an active nomination. One rule for the public ranking and
    /// for the jury's roster of works (<see cref="Jury.JuryEvaluationService"/>), so the two can never disagree.
    /// </summary>
    public static IQueryable<Submission> BallotWorks(MeydanDbContext db, int competitionId) =>
        db.Submissions.AsNoTracking().Where(s => s.CompetitionId == competitionId
                                                 && CompetitionResult.EligibleStatuses.Contains(s.Status)
                                                 && s.ReferenceNumber != null
                                                 && s.CompetitionCategoryId != null
                                                 && s.Category!.IsActive);

    private async Task<Entry> EntryAsync(Gate gate, string reference, CancellationToken ct) =>
        (await ReadAsync(Voteable(gate.Competition.Id).Where(s => s.ReferenceNumber == reference), ct))
        .FirstOrDefault() ?? throw new NotFoundException(NotOnTheBallot);

    private Task<List<Entry>> ReadAsync(IQueryable<Submission> query, CancellationToken ct) =>
        query.Select(s => new Entry(
                s.Id, s.ReferenceNumber!, s.Title, s.UserId,
                s.CompetitionCategoryId!.Value, s.Category!.Slug, s.Category.Name,
                s.SubmittedAt, s.PreviewFileId,
                s.Files.OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
                    .Select(f => new EntryFile(f.Id, f.CompetitionFieldId, f.ContentType, f.PublicAssetStatus,
                        f.PublicAssetWidth, f.PublicAssetHeight, f.HiddenFromPublic))
                    .ToList()))
            .ToListAsync(ct);

    private Task<List<Nomination>> NominationsAsync(int competitionId, CancellationToken ct) =>
        db.CompetitionCategories.AsNoTracking()
            .Where(x => x.CompetitionId == competitionId && x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new Nomination(x.Id, x.Slug, x.Name))
            .ToListAsync(ct);

    // ---- Counting ---------------------------------------------------------------------------------
    //
    // Counted from the rows themselves, every time. No denormalised total: a second copy of the truth earns its
    // drift only once measurement says the query is too slow, and at this size it is not.

    private async Task<Dictionary<int, int>> VoteCountsAsync(IReadOnlyList<int> submissionIds, CancellationToken ct) =>
        submissionIds.Count == 0
            ? []
            : (await db.Votes.AsNoTracking()
                    .Where(v => submissionIds.Contains(v.SubmissionId))
                    .GroupBy(v => v.SubmissionId)
                    .Select(g => new { Id = g.Key, Count = g.Count() })
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x.Count);

    private async Task<Dictionary<int, int>> VotesByCategoryAsync(int competitionId, CancellationToken ct) =>
        (await db.Votes.AsNoTracking()
                .Where(v => v.Category.CompetitionId == competitionId)
                .GroupBy(v => v.CompetitionCategoryId)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.Count);

    // ---- The viewer -------------------------------------------------------------------------------

    private async Task<Voter?> VoterAsync(Gate gate, Guid? userId, CancellationToken ct)
    {
        if (userId is not { } id) return null;
        var account = await db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => new { u.CreatedAt, u.EmailConfirmed }).FirstOrDefaultAsync(ct);
        if (account is null) return null;
        // Every vote this person cast, including one set aside: the page must still say they have voted.
        var cast = await db.Votes.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.UserId == id && v.Category.CompetitionId == gate.Competition.Id)
            .Select(v => new { v.CompetitionCategoryId, v.SubmissionId })
            .ToListAsync(ct);
        return new Voter(id, cast.ToDictionary(x => x.CompetitionCategoryId, x => x.SubmissionId),
            confirmation.Requires(account.CreatedAt, account.EmailConfirmed));
    }

    private async Task<VotingViewerDto?> ViewerAsync(
        Gate gate, Voter? voter, IReadOnlyList<Nomination> nominations, CancellationToken ct)
    {
        if (voter is null) return null;
        // One vote per competition: once it is cast, nothing on the ballot is open to this viewer any more.
        var reason = gate.State != VotingState.Open ? VoteEligibility.VotingNotOpen
            : voter.VotesByCategory.Count > 0 ? VoteEligibility.AlreadyVotedInNomination
            : voter.NeedsEmail ? VoteEligibility.EmailNotConfirmed
            : VoteEligibility.Allowed;
        var slugs = nominations.Where(n => voter.VotesByCategory.ContainsKey(n.Id)).Select(n => n.Slug).ToList();
        var voted = voter.VotesByCategory.Values.ToList();
        var references = voted.Count == 0
            ? []
            : await db.Submissions.AsNoTracking()
                .Where(s => voted.Contains(s.Id) && s.ReferenceNumber != null)
                .Select(s => s.ReferenceNumber!)
                .ToListAsync(ct);
        return new VotingViewerDto(reason == VoteEligibility.Allowed, reason, slugs, references);
    }

    /// <summary>Why the button on this particular work looks the way it does.</summary>
    private static VoteEligibility EligibilityFor(Gate gate, Entry e, Voter? voter)
    {
        if (voter is null)
            return gate.State == VotingState.Open ? VoteEligibility.NotSignedIn : VoteEligibility.VotingNotOpen;
        // The work this viewer voted for says so first, whether or not the window is still open: the answer to
        // "did mine count?" does not change when voting ends. Every other work says the window has shut, or — while
        // it is open — that the one vote is spent: one vote per competition, so a vote for any work, in any
        // category, closes all the others.
        if (voter.VotesByCategory.Values.Contains(e.Id)) return VoteEligibility.AlreadyVotedForThisWork;
        if (gate.State != VotingState.Open) return VoteEligibility.VotingNotOpen;
        if (voter.VotesByCategory.Count > 0) return VoteEligibility.AlreadyVotedInNomination;
        if (e.OwnerId == voter.UserId) return VoteEligibility.OwnWork;
        if (voter.NeedsEmail) return VoteEligibility.EmailNotConfirmed;
        // Any signed-in account votes, however recently it was opened: the owner lifted D11 on 4 October 2026.
        // The admin tally says instead how many votes came from accounts opened after voting began.
        return VoteEligibility.Allowed;
    }

    // ---- Shaping ----------------------------------------------------------------------------------

    private static ResultCompetitionDto CompetitionDto(Competition c) =>
        new(c.Slug, c.Title, c.ShortDescription, c.CategoryLabel, c.HasCategories,
            new ProjectRefDto(c.Project.Slug, c.Project.Name, c.Project.AccentColor));

    private static VotingWorkDto Card(Gate gate, Entry e, int votes, Voter? voter, string? concept)
    {
        var (image, state) = Preview(gate, e);
        return new VotingWorkDto(e.Reference,
            gate.TitleIsPublic && !string.IsNullOrWhiteSpace(e.Title) ? e.Title : null,
            new GalleryCategoryDto(e.CategorySlug, e.CategoryName),
            image, state, votes, EligibilityFor(gate, e, voter), concept);
    }

    /// <summary>
    /// The description line for a page of works: one query, for one field, over the ids on the page.
    ///
    /// <para>
    /// Not a query per work, and not every answer pulled back to be filtered in memory — the field is named in
    /// the WHERE clause, so a private answer is never read at all, let alone sent. Two columns come back.
    /// </para>
    /// </summary>
    private async Task<Dictionary<int, string>> ConceptsAsync(
        Gate gate, IReadOnlyList<int> submissionIds, CancellationToken ct)
    {
        if (gate.ConceptFieldId is not { } fieldId || submissionIds.Count == 0) return [];
        var rows = await db.SubmissionAnswers.AsNoTracking()
            .Where(a => a.CompetitionFieldId == fieldId
                        && submissionIds.Contains(a.SubmissionId)
                        && a.Value != null && a.Value != "")
            .Select(a => new { a.SubmissionId, a.Value })
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.SubmissionId, x => Shorten(x.Value!));
    }

    /// <summary>
    /// A card's worth of it. Whitespace is collapsed so a paragraph break cannot stretch a card, and a long
    /// answer is cut at a word so it reads as a sentence rather than a severed one; the whole text is on the
    /// work's own page.
    /// </summary>
    private const int ConceptPreviewLength = 220;

    private static string Shorten(string value)
    {
        var text = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= ConceptPreviewLength) return text;
        var cut = text.LastIndexOf(' ', ConceptPreviewLength);
        return string.Concat(text.AsSpan(0, cut > ConceptPreviewLength / 2 ? cut : ConceptPreviewLength).TrimEnd(), "…");
    }

    /// <summary>
    /// Files of public File fields, whatever state their derivative is in — except those an admin hid (a picture
    /// that names its author, D2): those do not exist as far as the ballot is concerned.
    /// </summary>
    private static List<EntryFile> PublicFieldFiles(Gate gate, Entry e)
    {
        var fields = gate.PublicFileFieldIds;
        return e.Files.Where(f => !f.Hidden && f.FieldId is { } id && fields.Contains(id)).ToList();
    }

    /// <summary>Those whose public-safe copy is ready. A pending or failed one stays hidden; an original never appears.</summary>
    private static List<EntryFile> PublicFiles(Gate gate, Entry e) =>
        PublicFieldFiles(gate, e).Where(f => f.Asset == PublicAssetStatus.Ready).ToList();

    private static bool IsImage(EntryFile f) => f.ContentType.StartsWith("image/", StringComparison.Ordinal);

    private static bool IsPdf(EntryFile f) => f.ContentType == "application/pdf";

    /// <summary>Where the private originals of these files are kept — read on the server only, to render pages.</summary>
    private async Task<Dictionary<int, string>> OriginalKeysAsync(Entry e, IEnumerable<EntryFile> files, CancellationToken ct)
    {
        var ids = files.Select(f => f.Id).ToList();
        if (ids.Count == 0) return [];
        return await db.SubmissionFiles.AsNoTracking()
            .Where(f => f.SubmissionId == e.Id && ids.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.StoragePath, ct);
    }

    private static string FileUrl(Gate gate, Entry e, int fileId) =>
        $"/api/competitions/{gate.Competition.Slug}/voting/works/{e.Reference}/files/{fileId}";

    private static (PublicImageDto? Image, PreviewState State) Preview(Gate gate, Entry e)
    {
        var ready = PublicFiles(gate, e);
        var chosen = ready.FirstOrDefault(f => f.Id == e.PreviewFileId) ?? ready.FirstOrDefault(IsImage) ?? ready.FirstOrDefault();
        if (chosen is not null)
        {
            var url = FileUrl(gate, e, chosen.Id);
            return (new PublicImageDto(url, url + "/thumb", chosen.Width, chosen.Height), PreviewState.Ready);
        }
        var all = PublicFieldFiles(gate, e);
        return all.Count == 0 ? (null, PreviewState.None)
            : all.Any(f => f.Asset == PublicAssetStatus.Pending) ? (null, PreviewState.Processing)
            : (null, PreviewState.Unavailable);
    }
}
