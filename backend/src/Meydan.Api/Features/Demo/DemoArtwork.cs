using SkiaSharp;

namespace Meydan.Api.Features.Demo;

/// <summary>
/// Prepares a supplied demo chevron for import. The asset pack's chevrons were cut from a contact sheet: each small
/// bitmap shows the shield on the sheet's dark slate card (lighter towards the top, sometimes with a strip of the
/// neighbouring card at an edge) and carries its own file name ("submissions/balaxani-01.webp") printed under the
/// shield. Imported as-is, that caption would appear inside the public artwork. When — and only when — that layout is
/// recognised, everything but the shield is cleared: the sheet (a low-saturation, bluish grey, reached from the
/// image border), the caption and any strip. The shield is cropped with a small margin on a transparent background
/// and enlarged 2× so it reads at card size. Anything else is returned unchanged. The source files are never modified.
/// </summary>
public static class DemoArtwork
{
    public sealed record Prepared(byte[] Png, bool Trimmed, int Width, int Height);

    private const int Upscale = 2;
    private const int Margin = 2;

    public static Prepared Prepare(byte[] source)
    {
        using var bitmap = SKBitmap.Decode(source) ?? throw new InvalidOperationException("demo artwork does not decode");
        var (w, h) = (bitmap.Width, bitmap.Height);
        var pixels = bitmap.Pixels; // unpremultiplied colours, whatever the decoded format

        // 1. The sheet: sheet-coloured pixels connected to the image border. The shield's gold rim stops the fill.
        var outside = new bool[w * h];
        var queue = new Queue<int>();
        void Visit(int i)
        {
            if (outside[i] || !IsSheet(pixels[i])) return;
            outside[i] = true;
            queue.Enqueue(i);
        }
        for (var x = 0; x < w; x++) { Visit(x); Visit((h - 1) * w + x); }
        for (var y = 0; y < h; y++) { Visit(y * w); Visit(y * w + w - 1); }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            var (x, y) = (i % w, i / w);
            if (x > 0) Visit(i - 1);
            if (x < w - 1) Visit(i + 1);
            if (y > 0) Visit(i - w);
            if (y < h - 1) Visit(i + w);
        }
        var foreground = Array.ConvertAll(outside, o => !o);

        // 2. The shield is the largest shape once hairline bridges are cut; the caption lies below it.
        var (labels, sizes, boxes) = Components(Dilate(Erode(foreground, w, h), w, h), w, h);
        if (sizes.Count == 0)
            return Unchanged(bitmap);
        var shield = sizes.IndexOf(sizes.Max());
        var box = boxes[shield];
        var below = Enumerable.Range(0, sizes.Count).Where(i => boxes[i].Top > box.Bottom).Sum(i => sizes[i]);
        var share = sizes[shield] / (double)(w * h);
        var looksExported = share is > 0.3 and < 0.9 && below > 0 && box.Width >= w / 2 && box.Height >= h / 2;
        if (!looksExported)
            return Unchanged(bitmap);

        // 3. Keep the shield (with the edge pixels the opening shaved off), crop, clear the rest, enlarge.
        var near = Dilate(Array.ConvertAll(labels, l => l == shield), w, h);
        var crop = SKRectI.Create(Math.Max(0, box.Left - Margin), Math.Max(0, box.Top - Margin), 0, 0);
        crop.Right = Math.Min(w, box.Right + 1 + Margin);
        crop.Bottom = Math.Min(h, box.Bottom + 1 + Margin);
        var cut = new SKColor[crop.Width * crop.Height];
        for (var y = 0; y < crop.Height; y++)
            for (var x = 0; x < crop.Width; x++)
            {
                var i = (crop.Top + y) * w + crop.Left + x;
                cut[y * crop.Width + x] = foreground[i] && near[i] ? pixels[i] : SKColors.Transparent;
            }
        // Alpha is only ever 0 (cleared, colour 0) or 255, so these colours are the same premultiplied or not.
        using var premultiplied = new SKBitmap(crop.Width, crop.Height, SKColorType.Rgba8888, SKAlphaType.Premul) { Pixels = cut };
        var info = new SKImageInfo(crop.Width * Upscale, crop.Height * Upscale, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var large = premultiplied.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
                          ?? throw new InvalidOperationException("demo artwork could not be scaled");
        return new Prepared(Encode(large), true, large.Width, large.Height);
    }

    /// <summary>The contact sheet's card: a low-saturation, bluish grey between near-black and light grey.</summary>
    internal static bool IsSheet(SKColor c)
    {
        int r = c.Red, g = c.Green, b = c.Blue;
        var saturation = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        var lightness = (r + g + b) / 3;
        return c.Alpha > 0 && saturation <= 22 && b >= r && b >= g - 2 && lightness is >= 25 and <= 150;
    }

    /// <summary>
    /// The supplied project covers are small banners that carry their own title and tagline on the left ("Xəzər sahili /
    /// Dənizin ruhu…"). The site prints the project's name over its cover, so the two titles would overlap. Keeps the
    /// text-free part from <paramref name="textFreeFrom"/> (a fraction of the width) to the right edge, without the 2 px
    /// frame of the export, as PNG. The source file is not modified.
    /// </summary>
    public static byte[] CropCover(byte[] source, double textFreeFrom)
    {
        if (textFreeFrom is <= 0 or >= 0.9)
            throw new ArgumentOutOfRangeException(nameof(textFreeFrom));
        using var bitmap = SKBitmap.Decode(source) ?? throw new InvalidOperationException("demo cover does not decode");
        const int frame = 2;
        var area = new SKRectI((int)Math.Ceiling(bitmap.Width * textFreeFrom), frame, bitmap.Width - frame, bitmap.Height - frame);
        using var part = new SKBitmap();
        if (!bitmap.ExtractSubset(part, area))
            throw new InvalidOperationException("demo cover could not be cropped");
        return Encode(part);
    }

    private static Prepared Unchanged(SKBitmap bitmap) => new(Encode(bitmap), false, bitmap.Width, bitmap.Height);

    private static bool[] Erode(bool[] mask, int w, int h) => Morph(mask, w, h, all: true);
    private static bool[] Dilate(bool[] mask, int w, int h) => Morph(mask, w, h, all: false);

    /// <summary>3×3 erosion (all neighbours set) or dilation (any neighbour set); outside the image counts as unset.</summary>
    private static bool[] Morph(bool[] mask, int w, int h, bool all)
    {
        var result = new bool[mask.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var hit = all;
                for (var dy = -1; dy <= 1 && hit == all; dy++)
                    for (var dx = -1; dx <= 1 && hit == all; dx++)
                    {
                        var (nx, ny) = (x + dx, y + dy);
                        var set = nx >= 0 && nx < w && ny >= 0 && ny < h && mask[ny * w + nx];
                        if (all ? !set : set) hit = !all;
                    }
                result[y * w + x] = hit;
            }
        return result;
    }

    /// <summary>4-connected components: a label per pixel (-1 = none), and each component's size and bounds.</summary>
    private static (int[] Labels, List<int> Sizes, List<SKRectI> Boxes) Components(bool[] mask, int w, int h)
    {
        var labels = new int[mask.Length];
        Array.Fill(labels, -1);
        var sizes = new List<int>();
        var boxes = new List<SKRectI>();
        var queue = new Queue<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] >= 0) continue;
            var label = sizes.Count;
            int count = 0, left = w, top = h, right = -1, bottom = -1;
            labels[start] = label;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                var (x, y) = (i % w, i / w);
                count++;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                foreach (var n in new[] { x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, y > 0 ? i - w : -1, y < h - 1 ? i + w : -1 })
                {
                    if (n < 0 || !mask[n] || labels[n] >= 0) continue;
                    labels[n] = label;
                    queue.Enqueue(n);
                }
            }
            sizes.Add(count);
            boxes.Add(new SKRectI(left, top, right, bottom)); // inclusive bounds
        }
        return (labels, sizes, boxes);
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                         ?? throw new InvalidOperationException("demo artwork could not be encoded");
        return data.ToArray();
    }
}
