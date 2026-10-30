using Meydan.Api.Common;
using Meydan.Infrastructure.Files;

namespace Meydan.Api.Features.Admin;

/// <summary>
/// Public cover images (projects, competitions, results banners). Same content checks as submission files —
/// extension, declared type and signature must agree — restricted to raster images, never SVG. The upload is decoded
/// and re-encoded as a metadata-free WEBP (no EXIF/GPS), and only that copy is stored — in the public "covers" area,
/// served by <see cref="MediaController"/>.
/// </summary>
public static class CoverImages
{
    public const string UrlPrefix = "/api/media/";
    private static readonly FileType[] Allowed = [FileTypes.Png, FileTypes.Jpeg, FileTypes.Webp];

    public static async Task<string> SaveAsync(
        IFormFile? file, UploadOptions policy, IFileStorage storage, PublicDerivatives derivatives, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            throw ValidationFailedException.For("cover", "Şəkil seçilməyib.");
        if (file.Length > policy.MaxFileSizeBytes)
            throw ValidationFailedException.For("cover", $"Şəkil ən çox {policy.MaxFileSizeMb} MB ola bilər.");

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return await SaveAsync(buffer.ToArray(), file.FileName, policy, storage, derivatives, ct);
    }

    /// <summary>The same pipeline for bytes that do not arrive as an upload (the demo seeder's asset files).</summary>
    public static async Task<string> SaveAsync(
        byte[] bytes, string? fileName, UploadOptions policy, IFileStorage storage, PublicDerivatives derivatives, CancellationToken ct)
    {
        if (bytes.Length == 0)
            throw ValidationFailedException.For("cover", "Şəkil seçilməyib.");
        if (bytes.Length > policy.MaxFileSizeBytes)
            throw ValidationFailedException.For("cover", $"Şəkil ən çox {policy.MaxFileSizeMb} MB ola bilər.");

        var name = FileNames.Sanitize(fileName);
        var byExtension = FileTypes.ByExtension(Path.GetExtension(name));
        if (byExtension is null || !Allowed.Contains(byExtension))
            throw ValidationFailedException.For("cover", "Örtük şəkli PNG, JPG və ya WEBP olmalıdır.");
        if (FileTypes.DetectBySignature(bytes.AsSpan(0, Math.Min(bytes.Length, FileTypes.SignatureLength))) != byExtension)
            throw ValidationFailedException.For("cover", $"“{name}” faylının məzmunu {byExtension.Label} formatına uyğun deyil.");

        PublicImage image;
        try
        {
            image = await derivatives.FromRasterAsync(bytes, ct);
        }
        catch (DerivativeException ex)
        {
            throw ValidationFailedException.For("cover", ex.Message);
        }
        var key = await storage.SaveAsync(new MemoryStream(image.Bytes, writable: false), ".webp", ct, StorageAreas.Covers);
        return UrlPrefix + key;
    }

    /// <summary>Deletes a cover previously stored by <see cref="SaveAsync(byte[], string?, UploadOptions, IFileStorage, PublicDerivatives, CancellationToken)"/>; external or legacy values are left alone.</summary>
    public static void DeleteIfOwned(string? url, IFileStorage storage)
    {
        if (url is null || !url.StartsWith(UrlPrefix, StringComparison.Ordinal))
            return;
        var key = url[UrlPrefix.Length..];
        if (!StorageAreas.IsIn(key, StorageAreas.Covers))
            return;
        try { storage.Delete(key); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* best effort */ }
    }
}
