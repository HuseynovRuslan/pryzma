using System.Text.RegularExpressions;
using Meydan.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Common;

/// <summary>
/// One phone number, one account (docs/voting/OWNER-DECISIONS.md, D12). On 5 October 2026 one person opened some
/// eighty voting accounts on one phone; an e-mail address costs nothing to invent, a phone number does.
/// <para>
/// Numbers are compared by their last nine digits, so "+994 50 123 45 67", "050-123-45-67" and "0501234567" are
/// one number — the same reading the vote's suspicion score uses. The rule is checked when an account is opened and
/// when a phone is changed; it is not a database constraint, because accounts that already share a number (147 on
/// that day) must keep working. Two registrations at the very same instant could both pass the check; that is the
/// accepted cost of not touching existing accounts, and the suspicion score still sees them.
/// </para>
/// </summary>
public static class PhoneNumbers
{
    public const string AlreadyUsed =
        "Bu telefon nömrəsi ilə artıq hesab açılıb. Həmin hesaba daxil olun və ya şifrəni bərpa edin.";

    public const string NotAMobile =
        "Azərbaycan mobil nömrəsini yazın, məsələn 050 123 45 67 və ya +994 50 123 45 67.";

    /// <summary>Azerbaijani mobile operator codes: Azercell 10 50 51, Bakcell 55 99, Nar 70 77, Naxtel 60.</summary>
    public static readonly IReadOnlySet<string> MobileCodes = new HashSet<string> { "10", "50", "51", "55", "60", "70", "77", "99" };

    /// <summary>
    /// The nine-digit national number of an Azerbaijani mobile, or null when the value is not one. Whatever separators
    /// were typed (spaces, dashes, brackets, a leading +) only the digits count, in one of these shapes:
    /// <list type="bullet">
    /// <item><c>501234567</c> — the nine digits;</item>
    /// <item><c>0501234567</c> — with the trunk 0;</item>
    /// <item><c>994501234567</c> — with the country code (typed as +994 …);</item>
    /// <item><c>9940501234567</c> — country code and trunk 0 together, as "+994 050 …" is often written;</item>
    /// <item><c>00994501234567</c> — the international 00 prefix.</item>
    /// </list>
    /// The first two of the nine digits must be a mobile operator code. A landline, a foreign number and a string of
    /// digits of another length are not accepted. A valid shape proves nothing about who holds the number: it is not
    /// verified, only plausible (docs/voting/OWNER-DECISIONS.md, D12).
    /// </summary>
    public static string? AzMobile(string? phone)
    {
        var d = Digits(phone);
        var national = d.Length switch
        {
            9 => d,
            10 when d.StartsWith('0') => d[1..],
            12 when d.StartsWith("994", StringComparison.Ordinal) => d[3..],
            13 when d.StartsWith("9940", StringComparison.Ordinal) => d[4..],
            14 when d.StartsWith("00994", StringComparison.Ordinal) => d[5..],
            _ => null,
        };
        return national is not null && MobileCodes.Contains(national[..2]) ? national : null;
    }

    /// <summary>Only the digits of a value, in order.</summary>
    public static string Digits(string? phone) => Regex.Replace(phone ?? "", @"\D", "");

    /// <summary>The comparable part of a number: its last nine digits, or empty when it has fewer.</summary>
    public static string Key(string? phone)
    {
        var digits = Regex.Replace(phone ?? "", @"\D", "");
        return digits.Length >= 9 ? digits[^9..] : "";
    }

    /// <summary>
    /// Whether another account already holds this number. In SQL as a POSIX match on the stored text — its last nine
    /// digits, with anything that is not a digit allowed between and after them — so no row is read into memory.
    /// </summary>
    public static Task<bool> InUseAsync(IQueryable<ApplicationUser> users, string? phone, Guid? except, CancellationToken ct)
    {
        var key = Key(phone);
        if (key.Length == 0) return Task.FromResult(false);
        var pattern = string.Join(@"\D*", key.ToCharArray()) + @"\D*$";
        return users.AnyAsync(u => u.PhoneNumber != null
                                   && (except == null || u.Id != except)
                                   && Regex.IsMatch(u.PhoneNumber, pattern), ct);
    }
}
