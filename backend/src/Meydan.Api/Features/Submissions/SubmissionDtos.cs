using Meydan.Api.Features.Catalog;
using Meydan.Domain.Enums;

namespace Meydan.Api.Features.Submissions;

public sealed record StartSubmissionRequest(string CompetitionSlug, string? CategorySlug);

public sealed record SaveAnswersRequest(Dictionary<string, string?> Answers);

public sealed record AcceptedFileTypeDto(string Key, string Label, string MimeType, IReadOnlyList<string> Extensions);

public sealed record FileRulesDto(int MaxFiles, long MaxFileSizeBytes, IReadOnlyList<AcceptedFileTypeDto> Accept);

public sealed record FieldDto(
    int Id,
    string Key,
    string Label,
    CompetitionFieldType FieldType,
    string? Placeholder,
    string? HelpText,
    bool IsRequired,
    int SortOrder,
    IReadOnlyList<string>? Options,
    int? MaxLength,
    FileRulesDto? Files);

public sealed record SubmissionFileDto(
    int Id,
    string FieldKey,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset UploadedAt);

public sealed record SubmissionCompetitionDto(
    string Slug,
    string Title,
    CompetitionPhase Phase,
    bool IsAcceptingSubmissions,
    bool HasCategories,
    string? CategoryLabel);

public sealed record SubmissionCategoryDto(string Slug, string Name);

public sealed record SubmissionDto(
    int Id,
    SubmissionStatus Status,
    string? ReferenceNumber,
    string Title,
    bool IsEditable,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt,
    ProjectRefDto Project,
    SubmissionCompetitionDto Competition,
    SubmissionCategoryDto? Category,
    IReadOnlyList<FieldDto> Fields,
    IReadOnlyDictionary<string, string?> Answers,
    IReadOnlyList<SubmissionFileDto> Files);
