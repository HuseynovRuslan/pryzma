using System.Text.Json;
using Meydan.Infrastructure.Files;

namespace Meydan.Api.Features.Demo;

/// <summary>
/// ./demo-assets/manifest.json — the source of truth for which supplied file belongs where. Every referenced file must
/// exist inside the asset folder, be a real WEBP/PNG/JPEG by its signature (matching its extension), be a readable
/// image of the size the manifest records; anything else stops the seed before a single row is written.
/// </summary>
public sealed class DemoManifest
{
    public sealed record Asset(string File, string Key, byte[] Bytes, FileType Type);

    public required string Folder { get; init; }
    public required IReadOnlyDictionary<string, Asset> ProjectCovers { get; init; }       // by project name
    public required IReadOnlyDictionary<string, Asset> CompetitionCovers { get; init; }   // by manifest title
    public required IReadOnlyDictionary<string, Asset> Chevrons { get; init; }            // by settlement name
    public required Asset? ResultCover { get; init; }

    public static async Task<DemoManifest> LoadAsync(string folder, PublicDerivatives derivatives, CancellationToken ct)
    {
        var root = Path.GetFullPath(folder);
        var path = Path.Combine(root, "manifest.json");
        if (!File.Exists(path))
            throw new InvalidOperationException($"manifest.json not found in {root}");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        var doc = json.RootElement;

        async Task<Asset> Load(JsonElement item, string key)
        {
            var file = item.GetProperty("file").GetString() ?? throw new InvalidOperationException("manifest entry without file");
            var full = Path.GetFullPath(Path.Combine(root, file));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException($"manifest file escapes the asset folder: {file}");
            if (!File.Exists(full))
                throw new InvalidOperationException($"manifest file is missing: {file}");
            var bytes = await File.ReadAllBytesAsync(full, ct);
            var byExtension = FileTypes.ByExtension(Path.GetExtension(full));
            var detected = FileTypes.DetectBySignature(bytes.AsSpan(0, Math.Min(bytes.Length, FileTypes.SignatureLength)));
            if (detected is null || detected != byExtension || !PublicDerivatives.RasterTypes.Contains(detected))
                throw new InvalidOperationException($"{file} is not a PNG/JPEG/WEBP image by its content");
            ImageDimensions dimensions;
            try
            {
                dimensions = derivatives.Inspect(bytes); // a readable image within the pixel budget
            }
            catch (DerivativeException ex)
            {
                throw new InvalidOperationException($"{file} cannot be read as an image ({ex.Code})");
            }
            // The manifest records each image's size: a file swapped for another one is caught here.
            if (item.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Array && size.GetArrayLength() == 2
                && (size[0].GetInt32() != dimensions.Width || size[1].GetInt32() != dimensions.Height))
                throw new InvalidOperationException(
                    $"{file} is {dimensions.Width}×{dimensions.Height}, but manifest.json says {size[0].GetInt32()}×{size[1].GetInt32()}");
            return new Asset(file, key, bytes, detected);
        }

        async Task<Dictionary<string, Asset>> Section(string name, string keyProperty)
        {
            var result = new Dictionary<string, Asset>(StringComparer.Ordinal);
            if (!doc.TryGetProperty(name, out var items)) return result;
            foreach (var item in items.EnumerateArray())
            {
                var key = item.GetProperty(keyProperty).GetString() ?? "";
                if (!result.TryAdd(key, await Load(item, key)))
                    throw new InvalidOperationException($"manifest lists \"{key}\" twice in {name}");
            }
            return result;
        }

        var results = await Section("results", "title");
        return new DemoManifest
        {
            Folder = root,
            ProjectCovers = await Section("projects", "title"),
            CompetitionCovers = await Section("competitions", "title"),
            Chevrons = await Section("submissions", "settlement"),
            ResultCover = results.Values.FirstOrDefault(),
        };
    }

    /// <summary>The competition cover whose manifest title starts with the competition's short name.</summary>
    public Asset? CompetitionCover(string titlePrefix) =>
        CompetitionCovers.Where(kv => kv.Key.StartsWith(titlePrefix, StringComparison.Ordinal)).Select(kv => kv.Value).FirstOrDefault();
}
