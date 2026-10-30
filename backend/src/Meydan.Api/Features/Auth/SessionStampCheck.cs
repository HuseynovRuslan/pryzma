using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Meydan.Infrastructure.Auth;
using Meydan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Auth;

/// <summary>
/// Runs after a JWT's signature and lifetime check: the token is accepted only while the account still exists and
/// its security stamp is the one the token was issued with. A password reset rotates the stamp, so every access
/// token issued before it stops working on the next request (401); refresh tokens are revoked separately. Tokens
/// without the claim, or with a fingerprint that is not the full 256-bit digest, are refused too — the SPA then
/// refreshes once. The comparison is of the whole fingerprint, in constant time; a prefix never matches.
/// Cost: one primary-key lookup of a single column per authenticated request.
/// </summary>
public static class SessionStampCheck
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var claimed = principal?.FindFirst(AccessTokenService.StampClaim)?.Value;
        if (!Guid.TryParse(principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId)
            || claimed is not { Length: AccessTokenService.StampFingerprintLength })
        {
            context.Fail("Access token without a valid session stamp.");
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<MeydanDbContext>();
        var current = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);
        if (current is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(claimed), Encoding.ASCII.GetBytes(AccessTokenService.StampFingerprint(current.SecurityStamp))))
            context.Fail("Session ended (password changed or account removed).");
    }
}
