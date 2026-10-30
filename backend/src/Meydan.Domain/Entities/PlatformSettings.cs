namespace Meydan.Domain.Entities;

/// <summary>
/// The platform's own public words — its name and the few lines of copy that are about the platform rather than
/// about a competition. One row, edited from the admin, so renaming the site or rewriting its headline is an
/// afternoon in the admin and not a deployment.
///
/// Only what a visitor would read belongs here. Anything that decides behaviour — hosts, keys, limits — stays in
/// configuration, where it is reviewed and deployed.
/// </summary>
public class PlatformSettings
{
    /// <summary>Always 1: there is one platform. The row is created on first read if it is missing.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>The brand, as the header, the footer and the browser tab say it.</summary>
    public string PlatformName { get; set; } = PlatformDefaults.PlatformName;

    /// <summary>The home page's headline. One string: it may wrap, but it is not built from fixed words.</summary>
    public string HomeHeroTitle { get; set; } = PlatformDefaults.HomeHeroTitle;

    public string HomeHeroSubtitle { get; set; } = PlatformDefaults.HomeHeroSubtitle;

    /// <summary>The line beside the sign-in and registration forms.</summary>
    public string AuthPromoTitle { get; set; } = PlatformDefaults.AuthPromoTitle;

    /// <summary>The picture beside those forms; null keeps the drawn fallback the app ships with.</summary>
    public string? AuthPromoImage { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// What the platform says before anyone has edited it — and what the browser falls back to when the settings
/// request fails, so a site with an unreachable API is still the right site. Kept in step with the same constants
/// in the frontend (src/platform/defaults.ts).
/// </summary>
public static class PlatformDefaults
{
    public const string PlatformName = "PRIZMA";
    public const string HomeHeroTitle = "İdeyanı meydana çıxar";
    public const string HomeHeroSubtitle = "Yaradıcı layihələr və açıq müsabiqələr üçün vahid platforma";
    public const string AuthPromoTitle = "Bir hesab, bütün müsabiqələr";
}
