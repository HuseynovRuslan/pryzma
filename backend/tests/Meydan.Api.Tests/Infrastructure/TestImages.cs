using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace Meydan.Api.Tests.Infrastructure;

/// <summary>
/// Real, decodable test files — including ones carrying the private metadata the public pipeline must drop:
/// EXIF with GPS coordinates, camera model, author and timestamps; PNG text/EXIF chunks; PDF author info.
/// </summary>
public static class TestImages
{
    /// <summary>Markers planted in metadata; none of them may ever appear in a public response.</summary>
    public const string SecretAuthor = "Gizli Muellif Aysel";
    public const string SecretCamera = "SecretPhone X9";
    public const string SecretSoftware = "PrivateEditor 7";

    public static byte[] Png(int width = 64, int height = 48, SKColor? color = null) =>
        Encode(width, height, color ?? new SKColor(0x56, 0x65, 0x2A), SKEncodedImageFormat.Png);

    public static byte[] Jpeg(int width = 64, int height = 48) =>
        Encode(width, height, new SKColor(0xF0, 0xB3, 0x23), SKEncodedImageFormat.Jpeg);

    public static byte[] Webp(int width = 64, int height = 48) =>
        Encode(width, height, new SKColor(0x10, 0x26, 0x2B), SKEncodedImageFormat.Webp);

    /// <summary>
    /// A genuine photo-sized PNG of about <paramref name="approxBytes"/>: every byte is pixel data (random noise does
    /// not compress, so the encoded size follows width × height), nothing is padding.
    /// </summary>
    public static byte[] NoisePng(int approxBytes)
    {
        var side = (int)Math.Sqrt(approxBytes / 4.0);
        byte[] png = [];
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var info = new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var pixels = new byte[info.BytesSize];
            new Random(side).NextBytes(pixels);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            using (var image = SKImage.FromPixelCopy(info, pixels))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                png = data.ToArray();
            var ratio = approxBytes / (double)png.Length;
            if (Math.Abs(ratio - 1) < 0.02) break;
            side = (int)(side * Math.Sqrt(ratio));
        }
        return png;
    }

    /// <summary>A valid PNG followed by zero padding up to <paramref name="size"/> bytes (decoders stop at IEND).</summary>
    public static byte[] PaddedPng(int size)
    {
        var png = Png(8, 8);
        if (size <= png.Length) return png;
        var bytes = new byte[size];
        png.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>
    /// JPEG with an APP1 EXIF block: Make/Model/Artist/Software/DateTime, orientation, and a GPS IFD pointing at
    /// 40.4093 N, 49.8671 E (Baku). The image is <paramref name="width"/>×<paramref name="height"/> as stored; with
    /// orientation 6 it must be displayed rotated 90° (i.e. <paramref name="height"/>×<paramref name="width"/>).
    /// </summary>
    public static byte[] JpegWithExif(int width = 120, int height = 60, ushort orientation = 6)
    {
        var jpeg = Jpeg(width, height);
        var exif = BuildExif(orientation);
        // APP1 segment: FF E1, length (incl. itself), "Exif\0\0", TIFF data.
        var app1 = new List<byte> { 0xFF, 0xE1 };
        var length = 2 + 6 + exif.Length;
        app1.Add((byte)(length >> 8));
        app1.Add((byte)(length & 0xFF));
        app1.AddRange("Exif\0\0"u8.ToArray());
        app1.AddRange(exif);
        // Insert right after SOI (FF D8).
        return [.. jpeg.Take(2), .. app1, .. jpeg.Skip(2)];
    }

    /// <summary>PNG with tEXt (Author, Software) and an eXIf chunk carrying the same EXIF+GPS block.</summary>
    public static byte[] PngWithMetadata(int width = 64, int height = 48)
    {
        var png = Png(width, height);
        var iend = png.Length - 12; // last chunk: length(4) "IEND" CRC(4)
        var extra = new List<byte>();
        extra.AddRange(Chunk("tEXt", Encoding.Latin1.GetBytes("Author\0" + SecretAuthor)));
        extra.AddRange(Chunk("tEXt", Encoding.Latin1.GetBytes("Software\0" + SecretSoftware)));
        extra.AddRange(Chunk("eXIf", BuildExif(1)));
        return [.. png.Take(iend), .. extra, .. png.Skip(iend)];
    }

    /// <summary>A tiny file whose PNG header claims 100000×100000 pixels (10 gigapixels) — a decompression bomb.</summary>
    public static byte[] PngBomb()
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), 100_000);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 100_000);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        var idat = new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 }; // empty zlib stream
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", ihdr), .. Chunk("IDAT", idat), .. Chunk("IEND", [])];
    }

    /// <summary>One-page PDF (a filled square) with Author/Creator/Producer metadata.</summary>
    public static byte[] PdfWithMetadata()
    {
        var content = "0.34 0.40 0.16 rg 20 20 160 120 re f";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 160] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Author ({SecretAuthor}) /Creator ({SecretSoftware}) /Producer ({SecretCamera}) >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A PDF of <paramref name="pages"/> pages (each a filled square of its own shade) with planted metadata.</summary>
    public static byte[] PdfWithPages(int pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "" };
        var kids = new List<string>();
        for (var i = 0; i < pages; i++)
        {
            var content = $"0.{10 + i * 7 % 80} 0.40 0.16 rg 20 20 160 120 re f";
            var pageObj = objects.Count + 1;
            kids.Add($"{pageObj} 0 R");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 160] /Contents {pageObj + 1} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {pages} >>";
        objects.Add($"<< /Author ({SecretAuthor}) /Creator ({SecretSoftware}) /Producer ({SecretCamera}) >>");
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info {objects.Count} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>True if any of the planted metadata survived in <paramref name="bytes"/>.</summary>
    public static IEnumerable<string> LeakedMarkers(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        foreach (var marker in new[] { SecretAuthor, SecretCamera, SecretSoftware, "Exif", "GPS", "eXIf", "tEXt", "XMP", "ICC_PROFILE" })
            if (text.Contains(marker, StringComparison.Ordinal))
                yield return marker;
        // GPS rational for 40° N (40/1) as written by BuildExif.
        if (IndexOf(bytes, [0, 0, 0, 40, 0, 0, 0, 1]) >= 0) yield return "gps-latitude";
    }

    public static (int Width, int Height) Dimensions(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes)) ?? throw new InvalidOperationException("not an image");
        return (codec.Info.Width, codec.Info.Height);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static byte[] Encode(int w, int h, SKColor color, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(0, 0, w / 3f, h / 3f, paint); // marks the top-left corner (orientation checks)
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 90).ToArray();
    }

    /// <summary>Big-endian TIFF: IFD0 (Make, Model, Orientation, Software, DateTime, Artist, GPS pointer) + GPS IFD.</summary>
    private static byte[] BuildExif(ushort orientation)
    {
        var data = new List<byte>();
        var strings = new List<(int Offset, byte[] Bytes)>();
        // Layout: header(8) IFD0 at 8: count(2) + 7 entries*12 + next(4) = 90 → ends at 98.
        // GPS IFD at 98: count(2) + 4 entries*12 + next(4) = 54 → ends at 152. Then string/rational data.
        var dataStart = 152;
        int Add(byte[] bytes)
        {
            var off = dataStart + strings.Sum(s => s.Bytes.Length);
            strings.Add((off, bytes));
            return off;
        }
        byte[] Ascii(string s) => [.. Encoding.ASCII.GetBytes(s), 0];

        var make = Ascii("SecretMaker");
        var model = Ascii(SecretCamera);
        var software = Ascii(SecretSoftware);
        var date = Ascii("2026:09:18 10:11:12");
        var artist = Ascii(SecretAuthor);
        var latOff = Add([0, 0, 0, 40, 0, 0, 0, 1, 0, 0, 0, 24, 0, 0, 0, 1, 0, 0, 0, 33, 0, 0, 0, 1]);
        var lonOff = Add([0, 0, 0, 49, 0, 0, 0, 1, 0, 0, 0, 52, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]);
        var makeOff = Add(make);
        var modelOff = Add(model);
        var softOff = Add(software);
        var dateOff = Add(date);
        var artistOff = Add(artist);

        void U16(int v) { data.Add((byte)(v >> 8)); data.Add((byte)v); }
        void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
        void Entry(int tag, int type, int count, int valueOrOffset, bool shortValue = false)
        {
            U16(tag); U16(type); U32(count);
            if (shortValue) { U16(valueOrOffset); U16(0); } else U32(valueOrOffset);
        }

        data.AddRange("MM"u8.ToArray()); U16(42); U32(8);
        U16(7);
        Entry(0x010F, 2, make.Length, makeOff);
        Entry(0x0110, 2, model.Length, modelOff);
        Entry(0x0112, 3, 1, orientation, shortValue: true);
        Entry(0x0131, 2, software.Length, softOff);
        Entry(0x0132, 2, date.Length, dateOff);
        Entry(0x013B, 2, artist.Length, artistOff);
        Entry(0x8825, 4, 1, 98); // GPS IFD pointer
        U32(0);
        U16(4);
        Entry(0x0001, 2, 2, 'N' << 24);          // GPSLatitudeRef "N\0" inline
        Entry(0x0002, 5, 3, latOff);             // GPSLatitude
        Entry(0x0003, 2, 2, 'E' << 24);          // GPSLongitudeRef
        Entry(0x0004, 5, 3, lonOff);             // GPSLongitude
        U32(0);
        foreach (var (_, bytes) in strings) data.AddRange(bytes);
        return [.. data];
    }

    private static byte[] Chunk(string type, byte[] body)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var chunk = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)body.Length);
        typeBytes.CopyTo(chunk, 4);
        body.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + body.Length), Crc32([.. typeBytes, .. body]));
        return chunk;
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
