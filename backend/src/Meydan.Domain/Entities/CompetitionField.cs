using Meydan.Domain.Enums;

namespace Meydan.Domain.Entities;

/// <summary>
/// One input on a competition's submission form, defined by an admin — the form is never hardcoded.
/// OptionsJson depends on <see cref="FieldType"/>:
///   Select → ["Birinci", "İkinci"]
///   File   → {"maxFiles": 5, "maxFileSizeMb": 10, "accept": ["png", "jpeg", "webp", "pdf"]}
///            (each value can only narrow the global upload policy, never widen it)
///   ShortText / LongText → optional {"maxLength": 300}
/// </summary>
public class CompetitionField
{
    public int Id { get; set; }
    public int CompetitionId { get; set; }
    /// <summary>Stable machine key, unique within the competition; answers are addressed by it.</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public CompetitionFieldType FieldType { get; set; }
    public string? Placeholder { get; set; }
    public string? HelpText { get; set; }
    public bool IsRequired { get; set; }
    /// <summary>The answer to this field becomes the submission's display title (lists, İşlərim).</summary>
    public bool IsSubmissionTitle { get; set; }
    public string? OptionsJson { get; set; }
    public int SortOrder { get; set; }
    /// <summary>
    /// Disabled fields disappear from the form for new and draft work but stay attached to answers already
    /// given — fields that have answers are disabled, never deleted.
    /// </summary>
    public bool IsActive { get; set; } = true;
    /// <summary>
    /// Whether answers to this field (or, for File fields, its files) are shown in the public gallery once the
    /// competition's results are published. Off by default: nothing a participant typed or uploaded becomes
    /// public unless an admin decided so for that field.
    /// </summary>
    public bool IsPublicAfterResults { get; set; }

    public Competition Competition { get; set; } = null!;
}
