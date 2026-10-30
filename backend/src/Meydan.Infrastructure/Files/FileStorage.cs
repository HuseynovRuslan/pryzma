using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meydan.Infrastructure.Files;

/// <summary>
/// Where uploaded bytes live. Business code only ever holds the opaque keys this returns, so a cloud store
/// (S3, MinIO…) is a new implementation registered in DI — no change to submission or project code.
/// </summary>
public interface IFileStorage
{
    /// <summary>Stores the content under a new random key and returns the key. The write is all-or-nothing.</summary>
    /// <param name="area">Optional namespace such as <see cref="StorageAreas.Covers"/>; null for private submission originals.</param>
    Task<string> SaveAsync(Stream content, string canonicalExtension, CancellationToken ct, string? area = null);
    Stream OpenRead(string key);
    void Delete(string key);

    /// <summary>Non-destructive readiness probe: the root exists and can be written, read and cleaned up.</summary>
    Task<bool> ProbeAsync(CancellationToken ct);
}

public static class StorageAreas
{
    /// <summary>Public project/competition cover images (already re-encoded, metadata-free).</summary>
    public const string Covers = "covers";

    /// <summary>
    /// Public-safe derivatives of submission files (re-encoded images, PDF page previews). Stored separately from
    /// the private originals; still served only through the result gate, never as static files.
    /// </summary>
    public const string Derived = "derived";

    public static bool IsIn(string key, string area) => key.StartsWith(area + "/", StringComparison.Ordinal);
}

/// <summary>
/// Local disk store. Keys are generated here ("yyyy/MM/{32 hex}.ext") and validated on every access, so
/// neither user input nor a tampered database row can point outside the storage root. Writes go to a temporary
/// file in the target directory first and are renamed into place, so a crash or full disk never leaves a
/// half-written file under a real key.
/// </summary>
public sealed partial class LocalFileStorage : IFileStorage
{
    private const string TempSuffix = ".partial";
    private readonly string _root;
    private readonly TimeProvider _clock;

    [GeneratedRegex(@"^((covers|derived)/)?\d{4}/\d{2}/[0-9a-f]{32}\.(png|jpg|webp|pdf)$")]
    private static partial Regex KeyPattern();

    public LocalFileStorage(IOptions<UploadOptions> options, IHostEnvironment env, TimeProvider clock, ILogger<LocalFileStorage> logger)
    {
        _root = ResolveRoot(options.Value.StorageRoot, env.ContentRootPath);
        _clock = clock;
        Directory.CreateDirectory(_root);
        RemoveStaleTemporaryFiles(logger);
    }

    /// <summary>Absolute storage root; relative values resolve against the content root (development only).</summary>
    public static string ResolveRoot(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured));

    public async Task<string> SaveAsync(Stream content, string canonicalExtension, CancellationToken ct, string? area = null)
    {
        var now = _clock.GetUtcNow();
        var prefix = area is null ? "" : area + "/";
        var key = $"{prefix}{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{canonicalExtension}";
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + TempSuffix;
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(file, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return key;
    }

    public Stream OpenRead(string key) =>
        new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

    public void Delete(string key)
    {
        var path = Resolve(key);
        if (File.Exists(path)) File.Delete(path);
    }

    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        var probe = Path.Combine(_root, $".health-{Guid.NewGuid():N}{TempSuffix}");
        try
        {
            var payload = Encoding.ASCII.GetBytes("meydan");
            await File.WriteAllBytesAsync(probe, payload, ct);
            var back = await File.ReadAllBytesAsync(probe, ct);
            return back.AsSpan().SequenceEqual(payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TryDelete(probe);
        }
    }

    private string Resolve(string key)
    {
        if (!KeyPattern().IsMatch(key))
            throw new InvalidOperationException("Invalid storage key.");
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Storage key escapes the storage root.");
        return full;
    }

    /// <summary>Leftovers of writes interrupted by a crash; they never have a database row.</summary>
    private void RemoveStaleTemporaryFiles(ILogger logger)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime.AddHours(-1);
        var removed = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_root, "*" + TempSuffix, SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                TryDelete(path);
                removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not scan the storage root for interrupted writes ({Error})", ex.GetType().Name);
        }
        if (removed > 0) logger.LogInformation("Removed {Count} interrupted upload fragments", removed);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}

public static class FileNames
{
    private static readonly char[] Forbidden = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// Display-safe version of a client-supplied name: last path segment only, no control or reserved
    /// characters, bounded length, extension preserved. Metadata only — never used to build a path.
    /// </summary>
    public static string Sanitize(string? name)
    {
        var raw = (name ?? "").Replace('\\', '/');
        raw = raw[(raw.LastIndexOf('/') + 1)..];
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(c) || Forbidden.Contains(c)) continue;
            sb.Append(c);
        }
        var clean = sb.ToString().Trim().Trim('.').Trim();
        if (clean.Length == 0) return "fayl";

        const int max = 120;
        if (clean.Length > max)
        {
            var ext = Path.GetExtension(clean);
            ext = ext.Length is > 0 and <= 10 ? ext : "";
            clean = clean[..(max - ext.Length)] + ext;
        }
        return clean;
    }
}
