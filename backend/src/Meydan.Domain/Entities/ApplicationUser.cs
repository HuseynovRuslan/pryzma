using Microsoft.AspNetCore.Identity;

namespace Meydan.Domain.Entities;

/// <summary>One MEYDAN account, reused across every project and competition.</summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateTimeOffset TermsAcceptedAt { get; set; }
    /// <summary>A demo participant created by `seed-demo`: no password, locked out, never linked to QRLog.</summary>
    public bool IsDemo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<Submission> Submissions { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
}
