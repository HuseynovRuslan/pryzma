using Meydan.Api.Common;
using Meydan.Domain;
using Meydan.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meydan.Api.Features.Admin;

/// <summary>
/// Every admin endpoint derives from this: the Admin role is enforced on the server for each request,
/// whatever the SPA shows or hides.
/// </summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]
public abstract class AdminControllerBase : ControllerBase
{
    protected Guid CurrentUserId => User.GetUserId();
}

internal static class AdminSlugs
{
    public static string Resolve(string? requested, string fallbackSource)
    {
        var slug = Slug.From(string.IsNullOrWhiteSpace(requested) ? fallbackSource : requested);
        if (slug.Length == 0)
            throw ValidationFailedException.For("slug", "Ünvan (slug) üçün latın hərfləri və ya rəqəmlər lazımdır.");
        return slug.Length > 120 ? slug[..120].TrimEnd('-') : slug;
    }

    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
