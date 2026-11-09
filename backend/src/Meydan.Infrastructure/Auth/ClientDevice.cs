using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Meydan.Infrastructure.Auth;

/// <summary>
/// What a request says about the browser it came from: the User-Agent and the random id the site's own frontend keeps
/// in the browser (header <see cref="Header"/>). Stored with every sign-in and every vote as fraud evidence only —
/// docs/voting/OWNER-DECISIONS.md, D20. Both are client-supplied and therefore hints, never proof on their own.
/// </summary>
public static partial class ClientDevice
{
    /// <summary>The header the frontend sends on every API call.</summary>
    public const string Header = "X-Device-Id";

    /// <summary>A User-Agent is cut to this many characters before it is stored.</summary>
    public const int UserAgentMax = 512;

    /// <summary>A device id is 8–64 letters, digits or hyphens (the frontend sends a UUID); anything else is dropped.</summary>
    public const int DeviceIdMax = 64;

    [GeneratedRegex(@"\A[A-Za-z0-9-]{8,64}\z")]
    private static partial Regex DeviceIdShape();

    public static string? UserAgent(HttpContext? http)
    {
        var value = http?.Request.Headers.UserAgent.ToString().Trim();
        if (string.IsNullOrEmpty(value))
            return null;
        return value.Length <= UserAgentMax ? value : value[..UserAgentMax];
    }

    public static string? DeviceId(HttpContext? http)
    {
        var value = http?.Request.Headers[Header].ToString().Trim();
        return !string.IsNullOrEmpty(value) && DeviceIdShape().IsMatch(value) ? value : null;
    }
}
