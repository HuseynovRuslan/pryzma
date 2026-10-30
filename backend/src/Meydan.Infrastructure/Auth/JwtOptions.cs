namespace Meydan.Infrastructure.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "meydan";
    public string Audience { get; set; } = "meydan-web";
    /// <summary>HMAC-SHA256 signing key; at least 32 bytes. Must come from secrets in production.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    /// <summary>Lifetime when "Məni xatırla" is ticked.</summary>
    public int RefreshTokenDaysPersistent { get; set; } = 30;
    /// <summary>Lifetime of a browser-session login (cookie without Expires).</summary>
    public int RefreshTokenHoursSession { get; set; } = 12;
    public string RefreshCookieName { get; set; } = "meydan_rt";
}
