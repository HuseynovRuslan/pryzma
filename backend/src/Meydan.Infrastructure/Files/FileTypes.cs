namespace Meydan.Infrastructure.Files;

/// <summary>True when the leading bytes of a file are this format's signature ("magic number").</summary>
public delegate bool SignatureMatcher(ReadOnlySpan<byte> header);

/// <summary>A file format the platform knows how to verify by content, not just by name.</summary>
public sealed record FileType(string Key, string Label, string MimeType, string CanonicalExtension,
    IReadOnlyList<string> Extensions, IReadOnlyList<string> AcceptedDeclaredMimeTypes, SignatureMatcher Matches);

/// <summary>
/// Formats that can be accepted at all. Anything not listed here is rejected before any other rule runs.
/// SVG is deliberately absent: it is XML that can carry script and needs dedicated sanitising first.
/// </summary>
public static class FileTypes
{
    public const int SignatureLength = 16;

    public static readonly FileType Png = new("png", "PNG", "image/png", ".png", [".png"], ["image/png"],
        h => h.Length >= 8 && h[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));

    public static readonly FileType Jpeg = new("jpeg", "JPG", "image/jpeg", ".jpg", [".jpg", ".jpeg"],
        ["image/jpeg", "image/jpg", "image/pjpeg"],
        h => h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF);

    public static readonly FileType Webp = new("webp", "WEBP", "image/webp", ".webp", [".webp"], ["image/webp"],
        h => h.Length >= 12
             && h[..4].SequenceEqual("RIFF"u8)
             && h[8..12].SequenceEqual("WEBP"u8));

    public static readonly FileType Pdf = new("pdf", "PDF", "application/pdf", ".pdf", [".pdf"],
        ["application/pdf", "application/x-pdf"],
        h => h.Length >= 5 && h[..5].SequenceEqual("%PDF-"u8));

    public static readonly IReadOnlyList<FileType> All = [Png, Jpeg, Webp, Pdf];

    public static FileType? ByKey(string key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    public static FileType? ByExtension(string extension) =>
        All.FirstOrDefault(t => t.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));

    public static FileType? DetectBySignature(ReadOnlySpan<byte> header)
    {
        foreach (var t in All)
            if (t.Matches(header)) return t;
        return null;
    }
}
