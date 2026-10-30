using System.Globalization;
using System.Text;

namespace Meydan.Domain.Common;

/// <summary>URL slugs with Azerbaijani-aware transliteration: "Bakı kəndləri" → "baki-kendleri".</summary>
public static class Slug
{
    private static readonly Dictionary<char, string> Map = new()
    {
        ['ə'] = "e", ['Ə'] = "e",
        ['ı'] = "i", ['I'] = "i", ['İ'] = "i",
        ['ş'] = "s", ['Ş'] = "s",
        ['ç'] = "c", ['Ç'] = "c",
        ['ğ'] = "g", ['Ğ'] = "g",
        ['ö'] = "o", ['Ö'] = "o",
        ['ü'] = "u", ['Ü'] = "u",
    };

    public static string From(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.Trim())
        {
            if (Map.TryGetValue(raw, out var mapped))
            {
                sb.Append(mapped);
                continue;
            }

            // Strip remaining diacritics (é → e) before the ASCII filter below.
            var baseChar = raw.ToString().Normalize(NormalizationForm.FormD)[0];
            var c = char.ToLowerInvariant(baseChar);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append('-');
        }

        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    public static bool IsValid(string slug) =>
        slug.Length is > 0 and <= 120
        && slug == From(slug);
}
