using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Meydan.Api.Common;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id)
            ? id
            : throw new AppException(StatusCodes.Status401Unauthorized, "Sessiyanın vaxtı bitib. Yenidən daxil olun.");
    }

    /// <summary>
    /// The signed-in user, or null when the request carries no session — for endpoints that answer anonymous and
    /// signed-in callers alike and shape the answer to the viewer rather than refusing one of them.
    /// </summary>
    public static Guid? TryGetUserId(this ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
