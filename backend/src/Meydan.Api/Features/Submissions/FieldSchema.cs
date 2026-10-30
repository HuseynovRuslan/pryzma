using System.Globalization;
using System.Text.Json;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;

namespace Meydan.Api.Features.Submissions;

/// <summary>Effective limits for one File field: the field's own OptionsJson narrowed by the global policy.</summary>
public sealed record FileRules(int MaxFiles, long MaxFileSizeBytes, IReadOnlyList<FileType> Accept);

/// <summary>A CompetitionField with its OptionsJson parsed and its limits resolved.</summary>
public sealed record FieldSchema(CompetitionField Field, IReadOnlyList<string> Options, int MaxLength, FileRules? Files)
{
    public const int ShortTextMax = 200;
    public const int LongTextMax = 5000;

    public string Key => Field.Key;
    public string Label => Field.Label;
    public CompetitionFieldType Type => Field.FieldType;

    public static FieldSchema From(CompetitionField field, UploadOptions uploads)
    {
        JsonElement? options = null;
        if (!string.IsNullOrWhiteSpace(field.OptionsJson))
        {
            try { options = JsonDocument.Parse(field.OptionsJson).RootElement.Clone(); }
            catch (JsonException) { /* invalid config behaves like "no options"; admin API rejects it on write */ }
        }

        var select = field.FieldType == CompetitionFieldType.Select && options is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];

        var defaultMax = field.FieldType == CompetitionFieldType.LongText ? LongTextMax : ShortTextMax;
        var maxLength = Math.Min(defaultMax, ReadInt(options, "maxLength") ?? defaultMax);

        FileRules? files = null;
        if (field.FieldType == CompetitionFieldType.File)
        {
            var globalTypes = uploads.AllowedFileTypes;
            var accept = globalTypes;
            if (options is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("accept", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                var wanted = a.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
                accept = globalTypes.Where(t => wanted.Contains(t.Key)).ToList();
            }
            var maxFiles = Math.Clamp(ReadInt(options, "maxFiles") ?? uploads.MaxFilesPerSubmission, 1, uploads.MaxFilesPerSubmission);
            var sizeMb = ReadInt(options, "maxFileSizeMb");
            var maxBytes = sizeMb is > 0 ? Math.Min(sizeMb.Value * 1024L * 1024L, uploads.MaxFileSizeBytes) : uploads.MaxFileSizeBytes;
            files = new FileRules(maxFiles, maxBytes, accept);
        }

        return new FieldSchema(field, select, maxLength, files);
    }

    private static int? ReadInt(JsonElement? options, string name) =>
        options is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.TryGetInt32(out var i)
            ? i
            : null;

    /// <summary>
    /// Checks the shape of a value and returns its normalised storage form. Emptiness is fine here —
    /// drafts may be incomplete; <see cref="RequiredError"/> runs at final submission.
    /// </summary>
    public (string? Value, string? Error) Normalize(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
            return (null, null);

        switch (Type)
        {
            case CompetitionFieldType.ShortText:
            case CompetitionFieldType.LongText:
                if (Type == CompetitionFieldType.ShortText && value.Contains('\n'))
                    value = value.ReplaceLineEndings(" ");
                return value.Length > MaxLength
                    ? (null, $"“{Label}” ən çox {MaxLength} simvol ola bilər.")
                    : (value, null);

            case CompetitionFieldType.Number:
                return decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                    ? (n.ToString(CultureInfo.InvariantCulture), null)
                    : (null, $"“{Label}” üçün rəqəm yazın.");

            case CompetitionFieldType.Date:
                return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? (d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null)
                    : (null, $"“{Label}” üçün düzgün tarix seçin.");

            case CompetitionFieldType.Url:
                return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && value.Length <= 2000
                    ? (uri.ToString(), null)
                    : (null, $"“{Label}” üçün tam link yazın (https://…).");

            case CompetitionFieldType.Select:
                return Options.Contains(value)
                    ? (value, null)
                    : (null, $"“{Label}” üçün siyahıdan birini seçin.");

            case CompetitionFieldType.Checkbox:
                return value is "true" or "false"
                    ? (value, null)
                    : (null, $"“{Label}” üçün dəyər düzgün deyil.");

            default:
                return (null, $"“{Label}” bu formada doldurulmur.");
        }
    }

    /// <summary>Message when a required field is missing at final submission, otherwise null.</summary>
    public string? RequiredError(string? value, int fileCount)
    {
        if (!Field.IsRequired) return null;
        return Type switch
        {
            CompetitionFieldType.File when fileCount == 0 => $"“{Label}” üçün ən azı bir fayl əlavə edin.",
            CompetitionFieldType.Checkbox when value != "true" => $"Davam etmək üçün “{Label}” qeyd olunmalıdır.",
            CompetitionFieldType.File or CompetitionFieldType.Checkbox => null,
            _ when string.IsNullOrWhiteSpace(value) => $"“{Label}” sahəsini doldurun.",
            _ => null,
        };
    }
}
