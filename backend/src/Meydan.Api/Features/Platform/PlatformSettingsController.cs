using System.ComponentModel.DataAnnotations;
using Meydan.Api.Common;
using Meydan.Api.Features.Admin;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Platform;

/// <summary>What the browser is told about the platform. Public on purpose, and nothing here is a secret.</summary>
public sealed record PublicSettingsDto(
    string PlatformName,
    string HomeHeroTitle,
    string HomeHeroSubtitle,
    string AuthPromoTitle,
    string? AuthPromoImage);

public sealed record PlatformSettingsRequest
{
    [Required(ErrorMessage = "Platformanın adı boş ola bilməz.")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "Platformanın adı 1–50 simvol olmalıdır.")]
    public string PlatformName { get; init; } = "";

    [Required(ErrorMessage = "Ana səhifə başlığı boş ola bilməz.")]
    [StringLength(120, MinimumLength = 1, ErrorMessage = "Ana səhifə başlığı 1–120 simvol olmalıdır.")]
    public string HomeHeroTitle { get; init; } = "";

    [Required(ErrorMessage = "Ana səhifə alt mətni boş ola bilməz.")]
    [StringLength(300, MinimumLength = 1, ErrorMessage = "Ana səhifə alt mətni 1–300 simvol olmalıdır.")]
    public string HomeHeroSubtitle { get; init; } = "";

    [Required(ErrorMessage = "Giriş səhifəsi başlığı boş ola bilməz.")]
    [StringLength(120, MinimumLength = 1, ErrorMessage = "Giriş səhifəsi başlığı 1–120 simvol olmalıdır.")]
    public string AuthPromoTitle { get; init; } = "";
}

/// <summary>
/// Reads the platform's own words. Anonymous, because the header needs them before anyone has signed in, and
/// unconditional, because a site whose settings request fails must still be the right site — the browser carries
/// the same defaults and falls back to them.
/// </summary>
[ApiController]
[Route("api/settings")]
public sealed class PlatformSettingsController(PlatformSettingsStore store) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<PublicSettingsDto> Get(CancellationToken ct) => Map(await store.GetAsync(ct));

    internal static PublicSettingsDto Map(PlatformSettings s) =>
        new(s.PlatformName, s.HomeHeroTitle, s.HomeHeroSubtitle, s.AuthPromoTitle, s.AuthPromoImage);
}

/// <summary>Editing those words: admin only, audited, and text only — never markup.</summary>
[Route("api/admin/settings")]
public sealed class AdminPlatformSettingsController(
    PlatformSettingsStore store,
    MeydanDbContext db,
    AuditTrail audit,
    IFileStorage storage,
    PublicDerivatives derivatives,
    IOptions<UploadOptions> uploads) : AdminControllerBase
{
    [HttpGet]
    public async Task<PublicSettingsDto> Get(CancellationToken ct) =>
        PlatformSettingsController.Map(await store.GetAsync(ct));

    [HttpPut]
    public async Task<PublicSettingsDto> Update(PlatformSettingsRequest req, CancellationToken ct)
    {
        var settings = await store.GetAsync(ct);
        settings.PlatformName = PlainText(req.PlatformName, nameof(req.PlatformName), "platformName");
        settings.HomeHeroTitle = PlainText(req.HomeHeroTitle, nameof(req.HomeHeroTitle), "homeHeroTitle");
        settings.HomeHeroSubtitle = PlainText(req.HomeHeroSubtitle, nameof(req.HomeHeroSubtitle), "homeHeroSubtitle");
        settings.AuthPromoTitle = PlainText(req.AuthPromoTitle, nameof(req.AuthPromoTitle), "authPromoTitle");
        return await SaveAsync(settings, "mətn", ct);
    }

    /// <summary>The picture beside the sign-in form, through the same pipeline as every other public image.</summary>
    [HttpPost("auth-image")]
    [UploadBodyLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 64L * 1024 * 1024)]
    public async Task<PublicSettingsDto> UploadAuthImage(CancellationToken ct)
    {
        var (_, file) = await UploadForms.ReadSingleFileAsync(Request, "image", uploads.Value, ct);
        var url = await CoverImages.SaveAsync(file, uploads.Value, storage, derivatives, ct);
        var settings = await store.GetAsync(ct);
        var previous = settings.AuthPromoImage;
        settings.AuthPromoImage = url;
        var saved = await SaveAsync(settings, "giriş vizualı", ct);
        CoverImages.DeleteIfOwned(previous, storage);
        return saved;
    }

    [HttpDelete("auth-image")]
    public async Task<PublicSettingsDto> RemoveAuthImage(CancellationToken ct)
    {
        var settings = await store.GetAsync(ct);
        var previous = settings.AuthPromoImage;
        settings.AuthPromoImage = null;
        var saved = await SaveAsync(settings, "giriş vizualı silindi", ct);
        CoverImages.DeleteIfOwned(previous, storage);
        return saved;
    }

    private async Task<PublicSettingsDto> SaveAsync(PlatformSettings settings, string what, CancellationToken ct)
    {
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(CurrentUserId, AuditActions.PlatformBrandSettingsUpdated, AuditEntities.Platform, "brand", what);
        await db.SaveChangesAsync(ct);
        return PlatformSettingsController.Map(settings);
    }

    /// <summary>
    /// These lines are shown as text and never as markup, so nothing has to be escaped at render time — but a
    /// value carrying a tag is refused here as well, rather than stored and silently displayed as literal
    /// characters that were plainly not meant.
    /// </summary>
    private static string PlainText(string value, string property, string field)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw ValidationFailedException.For(field, "Bu sahə boş ola bilməz.");
        if (trimmed.Contains('<') || trimmed.Contains('>'))
            throw ValidationFailedException.For(field, "HTML işarələri (< >) istifadə etmək olmaz — sadə mətn yazın.");
        return trimmed;
    }
}

/// <summary>
/// The single settings row, created on first read. Tracked, so a caller changes the object and saves through the
/// same unit of work as its audit entry.
/// </summary>
public sealed class PlatformSettingsStore(MeydanDbContext db)
{
    public async Task<PlatformSettings> GetAsync(CancellationToken ct)
    {
        var settings = await db.PlatformSettings.FirstOrDefaultAsync(x => x.Id == PlatformSettings.SingletonId, ct);
        if (settings is not null)
            return settings;

        settings = new PlatformSettings { Id = PlatformSettings.SingletonId, UpdatedAt = DateTimeOffset.UtcNow };
        db.PlatformSettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }
}
