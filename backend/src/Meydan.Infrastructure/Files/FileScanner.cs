using Meydan.Domain.Enums;

namespace Meydan.Infrastructure.Files;

public sealed record FileScanResult(FileScanStatus Status, string? Engine = null)
{
    public static readonly FileScanResult NotScanned = new(FileScanStatus.NotScanned);
}

/// <summary>
/// Extension point for an antivirus / content scanner (e.g. ClamAV). Called for every upload after the format
/// checks; an <see cref="FileScanStatus.Infected"/> result rejects the file. The default implementation scans
/// nothing and says so — the platform never claims a file was virus-checked when it was not.
/// </summary>
public interface IFileScanner
{
    Task<FileScanResult> ScanAsync(Stream content, FileType type, CancellationToken ct);
}

public sealed class NoFileScanner : IFileScanner
{
    public Task<FileScanResult> ScanAsync(Stream content, FileType type, CancellationToken ct) =>
        Task.FromResult(FileScanResult.NotScanned);
}
