using System.Runtime.Versioning;
using SkiaSharp;

namespace Meydan.Infrastructure.Files;

/// <summary>Why an image or PDF could not be turned into a public-safe derivative. Codes are safe to store and show.</summary>
public sealed class DerivativeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record ImageDimensions(int Width, int Height);

/// <summary>A freshly encoded public image: WEBP bytes that carry pixels only — no EXIF, GPS, XMP, ICC or text chunks.</summary>
public sealed record PublicImage(byte[] Bytes, int Width, int Height);

/// <summary>
/// Turns private originals into public-safe images. The original is only ever read; the result is a new
/// server-encoded WEBP built from decoded pixels, so nothing from the source container (metadata blocks,
/// thumbnails, comments, colour profiles, trailing data) can reach it.
/// <para>
/// Resources are bounded: dimensions are read from the header before any decoding and oversized images are
/// refused (decompression bombs), JPEGs are decoded directly at a reduced scale, the output is capped at
/// <see cref="UploadOptions.PublicImageMaxSide"/>, and only a few conversions run at once.
/// </para>
/// </summary>
public sealed class PublicDerivatives(UploadOptions options)
{
    // Registered as a singleton: this bounds CPU and memory for conversions across the whole process.
    private readonly SemaphoreSlim Gate = new(Math.Max(1, options.MaxConcurrentImageJobs));

    public static readonly FileType[] RasterTypes = [FileTypes.Png, FileTypes.Jpeg, FileTypes.Webp];

    /// <summary>
    /// Header-only check used at upload: the file must decode as the claimed raster format and stay within the
    /// pixel budget. Throws <see cref="DerivativeException"/> with a user-facing Azerbaijani message.
    /// </summary>
    public ImageDimensions Inspect(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
                          ?? throw new DerivativeException("unreadable", "Şəkil oxunmadı: fayl zədələnib və ya format dəstəklənmir.");
        var (w, h) = (codec.Info.Width, codec.Info.Height);
        EnsureWithinBudget(w, h);
        return new ImageDimensions(w, h);
    }

    public async Task<PublicImage> FromRasterAsync(byte[] bytes, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return Encode(DecodeOriented(bytes));
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// First page of a PDF rendered to pixels (no annotations, no form fields, no scripts — the bundled PDFium
    /// build has no JavaScript engine) and re-encoded like any image. The PDF itself is never published.
    /// </summary>
    public Task<PublicImage> FromPdfFirstPageAsync(byte[] bytes, CancellationToken ct) => FromPdfPageAsync(bytes, 0, ct);

    /// <summary>Any page of a PDF (0-based), made exactly as the first one is.</summary>
    public async Task<PublicImage> FromPdfPageAsync(byte[] bytes, int pageIndex, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            SKBitmap page;
            try
            {
                // PDFium ships for these platforms only (deployment targets Linux).
                // CA1416 does not follow the combined OS check; the runtime guard is right here.
#pragma warning disable CA1416
                page = OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? RenderPage(bytes, pageIndex, options.PublicImageMaxSide)
                    : throw new PlatformNotSupportedException();
#pragma warning restore CA1416
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new DerivativeException("pdf-unreadable", pageIndex == 0
                    ? "PDF-in birinci səhifəsi oxunmadı (zədələnmiş və ya şifrəli fayl)."
                    : $"PDF-in {pageIndex + 1}. səhifəsi oxunmadı.");
            }
            using (page)
            {
                EnsureWithinBudget(page.Width, page.Height);
                return Encode(Fit(page));
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Card-size copy of a derivative this class made (so it is already metadata-free): decoded, scaled down to
    /// <see cref="UploadOptions.ThumbnailMaxSide"/> — never up — and re-encoded. Transparency is kept.
    /// </summary>
    public async Task<PublicImage> ThumbnailAsync(byte[] derivative, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var data = SKData.CreateCopy(derivative);
            using var codec = SKCodec.Create(data)
                              ?? throw new DerivativeException("unreadable", "Önizləmə oxunmadı.");
            EnsureWithinBudget(codec.Info.Width, codec.Info.Height);
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
            using var full = new SKBitmap(info);
            var result = codec.GetPixels(info, full.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new DerivativeException("decode-failed", "Önizləmə oxunmadı.");
            return Encode(Fit(full, options.ThumbnailMaxSide), options.ThumbnailQuality);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>How many pages a PDF has, read by PDFium without rendering any.</summary>
    public int PdfPageCount(byte[] bytes)
    {
        try
        {
#pragma warning disable CA1416
            return OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? PDFtoImage.Conversion.GetPageCount(bytes)
                : throw new PlatformNotSupportedException();
#pragma warning restore CA1416
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DerivativeException("pdf-unreadable", "PDF oxunmadı (zədələnmiş və ya şifrəli fayl).");
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("macos")]
    private static SKBitmap RenderPage(byte[] bytes, int pageIndex, int side)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return PDFtoImage.Conversion.ToImage(stream, page: pageIndex, options: new PDFtoImage.RenderOptions(
            Dpi: 150, Width: side, Height: side, WithAnnotations: false, WithFormFill: false, WithAspectRatio: true));
    }

    // ---- Pixels ------------------------------------------------------------------------------------

    private void EnsureWithinBudget(int w, int h)
    {
        if (w <= 0 || h <= 0)
            throw new DerivativeException("unreadable", "Şəkil oxunmadı: ölçüləri düzgün deyil.");
        if (w > options.MaxImageSide || h > options.MaxImageSide || (long)w * h > options.MaxImagePixels)
            throw new DerivativeException("too-large",
                $"Şəklin ölçüsü çox böyükdür ({w}×{h}). Ən çox {options.MaxImageSide} piksel en/hündürlük və " +
                $"{options.MaxImagePixels / 1_000_000} megapiksel ola bilər.");
    }

    /// <summary>Decodes at most at the output size (scaled decode where the codec supports it) and applies EXIF orientation to the pixels.</summary>
    private SKBitmap DecodeOriented(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
                          ?? throw new DerivativeException("unreadable", "Şəkil oxunmadı: fayl zədələnib və ya format dəstəklənmir.");
        EnsureWithinBudget(codec.Info.Width, codec.Info.Height);

        var longest = Math.Max(codec.Info.Width, codec.Info.Height);
        var scale = Math.Min(1f, options.PublicImageMaxSide / (float)longest);
        var size = codec.GetScaledDimensions(scale);
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new DerivativeException("decode-failed", "Şəkil oxunmadı: fayl zədələnib.");
        }

        using (bitmap)
        {
            var fitted = Fit(bitmap);
            var oriented = ApplyOrigin(fitted, codec.EncodedOrigin);
            if (!ReferenceEquals(oriented, fitted)) fitted.Dispose();
            return oriented;
        }
    }

    /// <summary>Resizes (a copy) so the longest side is at most the public maximum.</summary>
    private SKBitmap Fit(SKBitmap source) => Fit(source, options.PublicImageMaxSide);

    /// <summary>Resizes (a copy) so the longest side is at most <paramref name="max"/>; never enlarges.</summary>
    private static SKBitmap Fit(SKBitmap source, int max)
    {
        var longest = Math.Max(source.Width, source.Height);
        var ratio = longest > max ? max / (double)longest : 1d;
        var info = new SKImageInfo(
            Math.Max(1, (int)Math.Round(source.Width * ratio)),
            Math.Max(1, (int)Math.Round(source.Height * ratio)),
            SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
               ?? throw new DerivativeException("decode-failed", "Şəkil emal olunmadı.");
    }

    private static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return src;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = swap ? (src.Height, src.Width) : (src.Width, src.Height);
        var dst = new SKBitmap(new SKImageInfo(w, h, src.ColorType, src.AlphaType, src.ColorSpace));
        using var canvas = new SKCanvas(dst);
        // Canvas operations apply to points in reverse order: the last call transforms first.
        // W/H are the source size; the destination is H×W for the four 90° cases.
        var (sw, sh) = (src.Width, src.Height);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: // mirror horizontally: (W−x, y)
                canvas.Translate(sw, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: // rotate 180°: (W−x, H−y)
                canvas.Translate(sw, sh); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: // mirror vertically: (x, H−y)
                canvas.Translate(0, sh); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: // transpose: (y, x)
                canvas.Scale(1, -1); canvas.RotateDegrees(-90); break;
            case SKEncodedOrigin.RightTop: // rotate 90° clockwise: (H−y, x)
                canvas.Translate(sh, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: // transverse: (H−y, W−x)
                canvas.Translate(0, sw); canvas.Scale(1, -1); canvas.Translate(sh, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: // rotate 90° counter-clockwise: (y, W−x)
                canvas.Translate(0, sw); canvas.RotateDegrees(-90); break;
        }
        using var image = SKImage.FromBitmap(src);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return dst;
    }

    private PublicImage Encode(SKBitmap bitmap) => Encode(bitmap, options.PublicImageQuality);

    private static PublicImage Encode(SKBitmap bitmap, int quality)
    {
        using (bitmap)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, quality)
                                ?? throw new DerivativeException("encode-failed", "Şəkil yenidən kodlaşdırılmadı.");
            return new PublicImage(encoded.ToArray(), bitmap.Width, bitmap.Height);
        }
    }
}
