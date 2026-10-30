using System.ComponentModel.DataAnnotations;
using Meydan.Api.Features.Catalog;
using Meydan.Domain.Enums;

namespace Meydan.Api.Features.Admin;

public sealed record Paged<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record StatusRequest<T>(T Status) where T : struct, Enum;

// ---- Projects ----------------------------------------------------------------------------------

/// <summary>Content of a project. Status and cover have their own endpoints so each change is deliberate and audited.</summary>
public sealed record ProjectUpsertRequest
{
    [Required(ErrorMessage = "Layihənin adını yazın."), StringLength(160, ErrorMessage = "Ad 160 simvoldan uzun ola bilməz.")]
    public string Name { get; init; } = "";

    /// <summary>Optional; generated from the name when empty.</summary>
    [StringLength(120)]
    public string? Slug { get; init; }

    [Required(ErrorMessage = "Qısa təsvir yazın."), StringLength(400, ErrorMessage = "Qısa təsvir 400 simvoldan uzun ola bilməz.")]
    public string ShortDescription { get; init; } = "";

    [StringLength(20000)]
    public string? Description { get; init; }

    [RegularExpression("^#[0-9A-Fa-f]{6}$", ErrorMessage = "Rəngi #RRGGBB formatında yazın.")]
    public string? AccentColor { get; init; }

    [Range(0, 100000)]
    public int SortOrder { get; init; }
}

public sealed record AdminProjectDto(
    int Id, string Name, string Slug, string ShortDescription, string? Description, string? CoverImage,
    string? AccentColor, ProjectStatus Status, int SortOrder, int CompetitionCount, int SubmissionCount,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool IsDemo)
{
    /// <summary>Only an empty project may be deleted; anything with content is archived instead.</summary>
    public bool CanDelete => CompetitionCount == 0;
}

// ---- Competitions ------------------------------------------------------------------------------

public sealed record CompetitionUpsertRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "Layihəni seçin.")]
    public int ProjectId { get; init; }

    [Required(ErrorMessage = "Müsabiqənin adını yazın."), StringLength(200, ErrorMessage = "Ad 200 simvoldan uzun ola bilməz.")]
    public string Title { get; init; } = "";

    [StringLength(120)]
    public string? Slug { get; init; }

    [Required(ErrorMessage = "Qısa təsvir yazın."), StringLength(500, ErrorMessage = "Qısa təsvir 500 simvoldan uzun ola bilməz.")]
    public string ShortDescription { get; init; } = "";

    [StringLength(20000)] public string? Description { get; init; }
    [StringLength(20000)] public string? Rules { get; init; }
    [StringLength(20000)] public string? Requirements { get; init; }

    /// <summary>One block per nomination: first line the name, the rest its explanation, blocks split by an empty line.</summary>
    [StringLength(20000)] public string? Nominations { get; init; }

    /// <summary>One criterion per line; no percentages unless the organiser actually set some.</summary>
    [StringLength(20000)] public string? JudgingCriteria { get; init; }

    /// <summary>What the organiser may do with a submitted work, and what stays with its author.</summary>
    [StringLength(20000)] public string? UsageTerms { get; init; }

    /// <summary>Shown next to the competition's own e-mail-free flow; leave empty when there is nothing to say.</summary>
    [StringLength(500)] public string? ContactNote { get; init; }

    public DateTimeOffset? StartDate { get; init; }
    public DateTimeOffset? EndDate { get; init; }
    public DateTimeOffset? ResultsAnnouncedOn { get; init; }
    public DateTimeOffset? ExhibitionOn { get; init; }

    /// <summary>
    /// When the public vote opens and closes. Deliberately separate from the entry dates: voting has to be able
    /// to open and close without anybody touching the deadline participants were given. Both empty: no vote.
    /// </summary>
    public DateTimeOffset? VotingStartsAt { get; init; }
    public DateTimeOffset? VotingEndsAt { get; init; }

    [StringLength(1000)]
    public string? Prize { get; init; }

    public bool HasCategories { get; init; }

    /// <summary>What the public page calls the categories: "Qəsəbələr", "Nominasiyalar", "Janrlar"…</summary>
    [StringLength(80)]
    public string? CategoryLabel { get; init; }

    [Range(1, 1000, ErrorMessage = "Limit 1 ilə 1000 arasında olmalıdır.")]
    public int? MaxSubmissionsPerUser { get; init; }

    [Range(0, 100000)]
    public int SortOrder { get; init; }
}

public sealed record AdminCompetitionDto(
    int Id, int ProjectId, string ProjectName, ProjectStatus ProjectStatus, string Title, string Slug,
    string ShortDescription, string? Description, string? Rules, string? Requirements, string? Nominations,
    string? JudgingCriteria, string? UsageTerms, string? ContactNote, string? CoverImage,
    DateTimeOffset? StartDate, DateTimeOffset? EndDate, DateTimeOffset? ResultsAnnouncedOn, DateTimeOffset? ExhibitionOn,
    string? Prize, CompetitionStatus Status,
    CompetitionPhase Phase, bool IsAcceptingSubmissions, bool HasCategories, string? CategoryLabel,
    int? MaxSubmissionsPerUser, int SortOrder, int CategoryCount, int ActiveCategoryCount, int FieldCount,
    int SubmissionCount, int DraftCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    ResultState ResultState, bool IsDemo,
    DateTimeOffset? VotingStartsAt, DateTimeOffset? VotingEndsAt, VotingState VotingState)
{
    /// <summary>Deleting is only possible before anyone has started a submission.</summary>
    public bool CanDelete => SubmissionCount == 0 && DraftCount == 0;
}

// ---- Categories --------------------------------------------------------------------------------

public sealed record CategoryUpsertRequest
{
    [Required(ErrorMessage = "Kateqoriyanın adını yazın."), StringLength(160)]
    public string Name { get; init; } = "";

    [StringLength(120)]
    public string? Slug { get; init; }

    [StringLength(2000)]
    public string? Description { get; init; }

    public bool IsActive { get; init; } = true;
}

public sealed record ImportCategoryItem(string Name, string? Slug, string? Description);

/// <summary>Adds many categories at once in the given order. All-or-nothing: any problem rejects the whole batch.</summary>
public sealed record ImportCategoriesRequest
{
    [Required, MinLength(1, ErrorMessage = "Ən azı bir sətir göndərin."), MaxLength(1000)]
    public IReadOnlyList<ImportCategoryItem> Items { get; init; } = [];
}

/// <summary>Legacy shortcut kept for scripts: names only, duplicates skipped.</summary>
public sealed record BulkCategoriesRequest
{
    [Required, MinLength(1, ErrorMessage = "Ən azı bir ad göndərin."), MaxLength(1000)]
    public IReadOnlyList<string> Names { get; init; } = [];
}

public sealed record ReorderRequest
{
    [Required, MinLength(1)]
    public IReadOnlyList<int> Ids { get; init; } = [];
}

public sealed record AdminCategoryDto(
    int Id, int CompetitionId, string Name, string Slug, string? Description, int SortOrder, bool IsActive,
    int SubmissionCount);

// ---- Form fields -------------------------------------------------------------------------------

/// <summary>
/// Create or change a form field. OptionsJson by type — Select: ["A","B"];
/// File: {"maxFiles":5,"maxFileSizeMb":10,"accept":["png","jpeg","webp","pdf"]}; text: {"maxLength":300}.
/// File limits may only tighten the global upload policy.
/// </summary>
public sealed record FieldUpsertRequest
{
    /// <summary>Optional; generated from the label when empty. Letters, digits, "-" and "_".</summary>
    [StringLength(60)]
    public string? Key { get; init; }

    [Required(ErrorMessage = "Sahənin adını yazın."), StringLength(200)]
    public string Label { get; init; } = "";

    public CompetitionFieldType FieldType { get; init; }

    [StringLength(200)] public string? Placeholder { get; init; }
    [StringLength(1000)] public string? HelpText { get; init; }

    public bool IsRequired { get; init; }
    public bool IsSubmissionTitle { get; init; }
    public bool IsActive { get; init; } = true;

    /// <summary>Show this field's answers (or files) in the public gallery once results are published.</summary>
    public bool IsPublicAfterResults { get; init; }

    [StringLength(4000)]
    public string? OptionsJson { get; init; }
}

public sealed record AdminFieldDto(
    int Id, int CompetitionId, string Key, string Label, CompetitionFieldType FieldType, string? Placeholder,
    string? HelpText, bool IsRequired, bool IsSubmissionTitle, bool IsActive, bool IsPublicAfterResults,
    string? OptionsJson, int SortOrder, int AnswerCount)
{
    /// <summary>Once participants answered it the type is fixed and the field can only be disabled.</summary>
    public bool IsLocked => AnswerCount > 0;
}

public sealed record UploadTypeDto(string Key, string Label, IReadOnlyList<string> Extensions);

public sealed record UploadPolicyDto(int MaxFileSizeMb, int MaxFilesPerSubmission, IReadOnlyList<UploadTypeDto> AllowedTypes);

// ---- Submissions -------------------------------------------------------------------------------

public sealed record AdminSubmissionQuery
{
    public int? ProjectId { get; init; }
    public int? CompetitionId { get; init; }
    public int? CategoryId { get; init; }
    public SubmissionStatus? Status { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    /// <summary>Reference number, participant name or e-mail.</summary>
    public string? Q { get; init; }
    /// <summary>true: only winners (of any category); false: everything else.</summary>
    public bool? Winner { get; init; }
    /// <summary>true: only demo content (`seed-demo`); false: only real submissions.</summary>
    public bool? Demo { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public sealed record RefDto(int Id, string Name);

// ---- Print / PDF ------------------------------------------------------------------------------------------------

/// <summary>
/// The works of a selection as management reviews them on paper. Built for the page and for nothing more: no
/// e-mail, no phone, no address, no registration data — ever. The author's name is there only when the admin asked
/// for it (<c>includeAuthor=true</c>); otherwise the field is null and the server never reads it.
/// </summary>
/// <param name="CompetitionTitle">The competition, when the selection is one competition's; null across several.</param>
/// <param name="Total">How many works match. More than <paramref name="Limit"/> are not sent; the page says so.</param>
public sealed record AdminPrintDto(
    string? CompetitionTitle, int Total, int Limit, bool AuthorIncluded, IReadOnlyList<AdminPrintItemDto> Items);

/// <param name="Category">The category, for a competition that has categories.</param>
/// <param name="Facts">Public choice answers that sort the works (e.g. "Nominasiya: …"), in the form's order.</param>
/// <param name="Concept">The public description — the first public long-text field that is not the title — in full.</param>
/// <param name="Image">The picture the gallery would show: a public field's file, never one hidden from the public.</param>
/// <param name="Author">"First Last", only with <c>includeAuthor=true</c> — internal documents only.</param>
/// <param name="PdfPages">Pages of the work's public PDFs together (0: no PDF), for the "PDF · N səhifə" badge.</param>
/// <param name="Material">The work's whole public material in reading order, for the appendix (PrintItems).</param>
public sealed record AdminPrintItemDto(
    int Id, string? Reference, string Title, string? Category, IReadOnlyList<AdminPrintFactDto> Facts,
    string? ConceptLabel, string? Concept, AdminPrintImageDto? Image, string? Author,
    int PdfPages, IReadOnlyList<AdminPrintMaterialDto> Material);

/// <summary>
/// One page of a work's material: a picture (<c>Kind</c> "image", its safe copy at /files/{id}/public) or a page of a
/// PDF ("pdf": page 1 is the file's safe copy, later pages /files/{id}/pages/{page}), with the PDF's page count.
/// </summary>
public sealed record AdminPrintMaterialDto(int FileId, string Kind, int Page, int Pages);

public sealed record AdminPrintFactDto(string Label, string Value);

/// <param name="Large">The metadata-free 1600 px copy is ready (/files/{id}/public); otherwise only the card-size copy.</param>
/// <param name="Page">For a PDF: the page that stands for the work (SubmissionImages.RepresentativePage) — the admin's
/// choice, else 2 of two or more pages, else 1. A page after the first comes from /files/{id}/pages/{page}.</param>
/// <param name="Pages">For a PDF: its page count (for the admin's "Önizləmə: 2/4"); 0 for a picture.</param>
public sealed record AdminPrintImageDto(int FileId, bool Large, int? Width, int? Height, int Page = 1, int Pages = 0);

public sealed record ParticipantRefDto(Guid Id, string Name, string? Email);

/// <param name="Preview">The file a list shows for this work (the admin's gallery choice, else the first image, else
/// the first file) and whether its small copy exists — lists never download originals.</param>
public sealed record AdminSubmissionListItem(
    int Id, string? ReferenceNumber, string Title, SubmissionStatus Status, DateTimeOffset? SubmittedAt,
    ParticipantRefDto Participant, RefDto Project, RefDto Competition, RefDto? Category, int FileCount, bool IsWinner,
    AdminPreviewDto? Preview, bool IsDemo);

/// <summary>Legacy per-competition list (kept for existing clients).</summary>
public sealed record AdminSubmissionDto(
    int Id, string? ReferenceNumber, string Title, SubmissionStatus Status, DateTimeOffset? SubmittedAt,
    string? CategoryName, Guid UserId, string UserFullName, string? UserEmail, int FileCount);

public sealed record AdminAnswerDto(
    int FieldId, string Key, string Label, string CurrentLabel, CompetitionFieldType FieldType, bool FieldActive, string? Value);

/// <param name="IsPublicField">The file's field is shown in the gallery once results are published.</param>
/// <param name="PublicAsset">Pending / Ready / Failed — state of the metadata-free public derivative.</param>
/// <param name="ScanStatus">NotScanned when no antivirus is configured (the default) — never claimed otherwise.</param>
/// <param name="ThumbnailReady">The small copy exists: lists show it instead of downloading the original.</param>
/// <param name="HiddenFromPublic">An admin took the file off every public page; it cannot be the preview then.</param>
public sealed record AdminFileDto(
    int Id, string? FieldKey, string? FieldLabel, string OriginalFileName, string ContentType, long SizeBytes,
    DateTimeOffset UploadedAt, bool IsPublicField, PublicAssetStatus PublicAsset, string? PublicAssetError,
    FileScanStatus ScanStatus, bool CanBePreview, bool ThumbnailReady, bool HiddenFromPublic,
    /// <summary>A PDF's page count, once its safe first page is ready (0 otherwise, and for anything but a PDF).</summary>
    int PdfPages = 0,
    /// <summary>The page an admin chose to stand for this PDF; null: automatic.</summary>
    int? PreviewPage = null,
    /// <summary>The page that stands for it everywhere (SubmissionImages.RepresentativePage): the choice, else automatic.</summary>
    int RepresentativePage = 1,
    /// <summary>The page the automatic choice would give (2 of two or more pages, else 1) — for the picker's label.</summary>
    int AutomaticPage = 1);

public sealed record AdminNoteDto(int Id, string Text, string AuthorName, DateTimeOffset CreatedAt);

public sealed record AdminStatusChangeDto(SubmissionStatus From, SubmissionStatus To, string? ChangedBy, DateTimeOffset ChangedAt, string? Comment);

public sealed record AdminParticipantDto(
    Guid Id, string FirstName, string LastName, string? Email, string? Phone, DateTimeOffset RegisteredAt);

public sealed record AdminSubmissionDetail(
    int Id, string? ReferenceNumber, string Title, SubmissionStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? SubmittedAt, AdminParticipantDto Participant, RefDto Project,
    RefDto Competition, string CompetitionSlug, string? CategoryLabel, RefDto? Category,
    IReadOnlyList<AdminAnswerDto> Answers, IReadOnlyList<AdminFileDto> Files, IReadOnlyList<AdminNoteDto> Notes,
    IReadOnlyList<AdminStatusChangeDto> History, IReadOnlyList<SubmissionStatus> AllowedStatuses,
    AdminSubmissionResultDto Result,
    /// <summary>Admin's explicit choice (null = automatic).</summary>
    int? PreviewFileId,
    /// <summary>What the gallery will actually show: the choice if still valid, else the automatic fallback.</summary>
    int? EffectivePreviewFileId,
    /// <summary>Demo content created by `seed-demo` (removed by `remove-demo`).</summary>
    bool IsDemo);

/// <summary>Null clears the choice (automatic preview).</summary>
public sealed record SetPreviewRequest(int? FileId);

public sealed record SetFileVisibilityRequest(bool Hidden);

/// <summary>The page of a PDF that stands for the work (1…its page count); null gives it back to the automatic choice.</summary>
public sealed record SetPdfPreviewPageRequest(int? Page);

public sealed record ChangeSubmissionStatusRequest
{
    public SubmissionStatus Status { get; init; }

    [StringLength(1000)]
    public string? Comment { get; init; }
}

public sealed record AddNoteRequest
{
    [Required(ErrorMessage = "Qeydin mətnini yazın."), StringLength(4000, ErrorMessage = "Qeyd 4000 simvoldan uzun ola bilməz.")]
    public string Text { get; init; } = "";
}

// ---- Dashboard, participants, audit -------------------------------------------------------------

/// <summary>
/// The overview. With <see cref="CompetitionId"/> set, the submission figures and the recent list are that
/// competition's only; the project and open-competition counts are always the platform's.
/// </summary>
public sealed record DashboardDto(
    int PublishedProjects, int OpenCompetitions, int TotalSubmissions, int DraftSubmissions,
    int SubmittedSubmissions, int UnderReviewSubmissions, IReadOnlyList<AdminSubmissionListItem> RecentSubmissions,
    int? CompetitionId, int ApprovedSubmissions, int RejectedSubmissions,
    IReadOnlyList<DashboardCompetitionDto> Competitions);

/// <summary>A competition the overview can be narrowed to; <see cref="IsOpen"/> is true while it takes entries.</summary>
public sealed record DashboardCompetitionDto(int Id, string Title, CompetitionStatus Status, CompetitionPhase Phase, bool IsOpen);

public sealed record AdminParticipantListItem(
    Guid Id, string Name, string? Email, string? Phone, DateTimeOffset RegisteredAt, int SubmissionCount, bool IsAdmin,
    bool IsDemo,
    /// <summary>The account has cast a vote, counted or set aside: it is never removed.</summary>
    bool HasVotes,
    /// <summary>An account with nothing of its own left, no vote, and not an admin's: the only kind that may be removed.</summary>
    bool CanDelete);

public sealed record AuditEntryDto(
    long Id, string Action, string EntityType, string EntityId, string? Summary, string? UserName, DateTimeOffset CreatedAt);

// ---- The public vote, from the administration's side -------------------------------------------

/// <summary>
/// One work in a nomination's tally. Here — and only here — a work is named with its participant: an admin
/// chooses a winner and has to know whose work it is. Nothing on this shape is public.
/// </summary>
public sealed record AdminVoteWorkDto(
    int SubmissionId, string Reference, string Title, string ParticipantName, SubmissionStatus Status,
    int VoteCount,
    /// <summary>1 for the most-voted work; works level on votes share a place.</summary>
    int Place,
    /// <summary>This work is already recorded as its nomination's winner.</summary>
    bool IsWinner,
    /// <summary>
    /// Of its votes, how many came from accounts opened after voting began. Such accounts may vote (D11 was
    /// lifted); a work whose votes are mostly theirs is worth a second look before it is given a prize.
    /// </summary>
    int LateVoteCount,
    /// <summary>Votes an admin set aside as fraudulent (D12): kept, and not in <c>VoteCount</c>.</summary>
    int InvalidatedVoteCount = 0,
    /// <summary>The work's picture for the ranking's thumbnail — a PDF's content page (PrintItems).</summary>
    AdminPrintImageDto? Image = null,
    /// <summary>Pages of the work's public PDFs together (0: none).</summary>
    int PdfPages = 0,
    /// <summary>
    /// The official place the work holds in the competition's result (1–3, or 1 as a category's winner), whether the
    /// result is published or not; null otherwise. Never the vote's rank — that is <c>Place</c>.
    /// </summary>
    int? OfficialPlace = null);

public sealed record AdminVotingNominationDto(
    int CategoryId, string Slug, string Name, bool IsActive, int WorkCount, int VoteCount,
    /// <summary>
    /// Two or more works are level at the top with at least one vote. The tally names no winner then: an admin
    /// chooses, because a machine picking by id or by timestamp would be arbitrary and unexplainable — D3.
    /// </summary>
    bool IsTie,
    IReadOnlyList<AdminVoteWorkDto> Works);

/// <summary>
/// One work as the suspicion score sees it: how its counted votes divide, and how many are probably not genuine.
/// <c>Votes</c>, the three bands and the estimates are over the votes that still count; the raw total and the votes
/// set aside are beside them.
/// </summary>
public sealed record AdminSuspicionWorkDto(
    int SubmissionId, string Reference, string Title, int Votes, int High, int Medium, int Low,
    /// <summary>The sum of its votes' scores over 100 — about how many of its votes are not genuine.</summary>
    double ExpectedFake,
    double CleanEstimate,
    /// <summary>Every vote it received, counted or not.</summary>
    int RawVotes,
    /// <summary>Set aside by an admin (D12).</summary>
    int InvalidatedVotes,
    /// <summary>Counted votes of a phone cluster (3+ voting accounts on one phone) — the only ones an admin may set aside.</summary>
    int PhoneClusterVotes,
    /// <summary>Counted votes that meet R2, coordinated identity — ground to set aside only while R2 is switched on.</summary>
    int CoordinatedIdentityVotes = 0);

/// <summary>A vote worth a second look (score 30 or more), with every reason that went into its score.</summary>
public sealed record AdminSuspiciousVoteDto(
    int Score, int SubmissionId, string Reference, string VoterName, DateTimeOffset SignedUpAt, DateTimeOffset VotedAt,
    IReadOnlyList<string> Reasons, long VoteId, VoteEvidence Evidence);

/// <summary>
/// One work's evidence, in numbers only (D12): no phone, e-mail or address — a repeated phone is a group size.
/// </summary>
public sealed record AdminWorkEvidenceDto(
    int SubmissionId, string Reference, string Title,
    int RawVotes, int ValidVotes, int InvalidatedVotes,
    /// <summary>Counted votes of a phone cluster — the only ones that may be set aside (D12).</summary>
    int PhoneClusterVotes,
    /// <summary>Votes from accounts whose phone another voting account of the competition shares.</summary>
    int RepeatedPhoneVotes, int LargestPhoneGroup,
    /// <summary>Votes from accounts whose full name another voting account shares.</summary>
    int RepeatedNameVotes, int LargestNameGroup,
    /// <summary>The most of this work's voters who share one connection — weak alone: networks share addresses.</summary>
    int LargestIpGroup,
    /// <summary>Voters who once used a connection the author used.</summary>
    int AuthorIpVotes,
    /// <summary>Voters whose phone is the author's own.</summary>
    int AuthorPhoneVotes,
    int NewAccountVotes,
    int? MedianSecondsToVote,
    /// <summary>The most votes from accounts opened during the vote within any 15 minutes.</summary>
    int LargestBurst15Min,
    DateTimeOffset? FirstSignUp, DateTimeOffset? LastSignUp, DateTimeOffset? FirstVote, DateTimeOffset? LastVote,
    /// <summary>Counted votes that meet R2, coordinated identity (<see cref="CoordinatedIdentityRule"/>).</summary>
    int CoordinatedIdentityVotes = 0,
    /// <summary>Whether R2 is ground to set a vote aside here, or only a review sign.</summary>
    bool CoordinatedIdentityEnabled = false);

/// <summary>
/// One vote under review. Groups are labels ("T1", "IP-2") with their size, never the phone or the address.
/// </summary>
public sealed record AdminReviewVoteDto(
    long VoteId, string VoterName, DateTimeOffset SignedUpAt, DateTimeOffset VotedAt,
    int Score, VoteEvidence Evidence, IReadOnlyList<string> Reasons,
    string? PhoneGroup, int PhoneGroupSize, string? NameGroup, int NameGroupSize, string? IpGroup, int IpGroupSize,
    DateTimeOffset? InvalidatedAt, string? InvalidatedBy, string? InvalidationReason,
    /// <summary>Meets R2, coordinated identity, whether or not the rule is switched on.</summary>
    bool CoordinatedIdentity = false);

public sealed record AdminWorkReviewDto(AdminWorkEvidenceDto Evidence, IReadOnlyList<AdminReviewVoteDto> Votes);

/// <summary>An admin's decision about chosen votes: their ids and, always, the reason.</summary>
public sealed record VoteDecisionRequest
{
    public IReadOnlyList<long>? VoteIds { get; init; }
    public string? Reason { get; init; }
}

/// <summary>How many votes the decision changed, and the works they belong to.</summary>
public sealed record AdminVoteDecisionDto(int Changed, IReadOnlyList<string> References);

/// <summary>D14 applied to the whole competition at once: the count the admin saw, and the reason.</summary>
public sealed record ApplyVotingRuleRequest
{
    /// <summary>The number of valid votes the admin was shown as meeting the rule; anything else changes nothing.</summary>
    public int ExpectedVotes { get; init; }
    /// <summary>Defaults to the owner-approved reason.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// D14 for one competition, for admins: the rule as frozen, what it meets now, per work and per domain, and the
/// evidence vote by vote with a masked address — never the address itself, a phone or a connection.
/// </summary>
public sealed record AdminDisposableEmailDto(
    string PolicyId, int Version, string EvidenceType, bool Enabled, string Reason, string Source, int DomainCount,
    DateTimeOffset? VotingStartsAt, DateTimeOffset? VotingEndsAt, bool VotingClosed,
    /// <summary>Valid votes meeting the rule now — what applying it would set aside.</summary>
    int EligibleVotes,
    /// <summary>Votes meeting the rule already set aside under it.</summary>
    int SetAsideUnderRule,
    /// <summary>Votes meeting the rule set aside earlier under another rule — never counted twice.</summary>
    int SetAsideOtherwise,
    /// <summary>Voting accounts with a listed domain opened before the vote: reported, never eligible.</summary>
    int OpenedBeforeVoting,
    IReadOnlyList<AdminRuleWorkDto> Works,
    IReadOnlyList<AdminRuleDomainDto> Domains,
    IReadOnlyList<AdminRuleEvidenceDto> Evidence);

public sealed record AdminRuleWorkDto(int SubmissionId, string Reference, int ValidNow, int Eligible, int ValidAfter, int SetAsideUnderRule);

public sealed record AdminRuleDomainDto(string Domain, string Provider, int Votes, int Eligible, int SetAsideUnderRule);

public sealed record AdminRuleEvidenceDto(
    long VoteId, string Reference, string MaskedEmail, string Domain, string Provider,
    DateTimeOffset AccountOpenedAt, DateTimeOffset VotedAt, bool Valid, bool SetAsideUnderRule);

/// <summary>
/// The suspicion score of the vote, for the admin who awards the places: a weighted list of signs, not a measured
/// probability. Names the accounts behind suspicious votes only — never anybody else's.
/// </summary>
public sealed record AdminVoteSuspicionDto(
    int VoteCount, int High, int Medium, int Low,
    IReadOnlyList<AdminSuspicionWorkDto> Works,
    IReadOnlyList<AdminSuspiciousVoteDto> Votes,
    /// <summary>Votes set aside by an admin, across the competition.</summary>
    int InvalidatedCount);

public sealed record AdminVotingDto(
    VotingState State,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    /// <summary>How many people have voted here at all — never who.</summary>
    int VoterCount,
    int VoteCount,
    /// <summary>Of the voters, how many opened their account after voting began.</summary>
    int LateVoterCount,
    IReadOnlyList<AdminVotingNominationDto> Nominations,
    /// <summary>
    /// Final works that belong to no nomination, so nobody can vote for them. Empty is the ordinary case; a row
    /// here is something to put right rather than something to hide.
    /// </summary>
    IReadOnlyList<AdminVoteWorkDto> Unplaced,
    /// <summary>Votes set aside by an admin (D12); <c>VoteCount</c> is the counted ones.</summary>
    int InvalidatedVoteCount = 0);
