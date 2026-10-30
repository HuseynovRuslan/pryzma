using Microsoft.AspNetCore.Identity;

namespace Meydan.Infrastructure.Auth;

/// <summary>Identity validation messages in Azerbaijani, since they reach the UI verbatim.</summary>
public sealed class AzerbaijaniIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DuplicateEmail(string email) =>
        new() { Code = nameof(DuplicateEmail), Description = "Bu e-mail ilə artıq hesab var." };

    public override IdentityError DuplicateUserName(string userName) =>
        new() { Code = nameof(DuplicateUserName), Description = "Bu e-mail ilə artıq hesab var." };

    public override IdentityError InvalidEmail(string? email) =>
        new() { Code = nameof(InvalidEmail), Description = "E-mail ünvanı düzgün deyil." };

    public override IdentityError PasswordTooShort(int length) =>
        new() { Code = nameof(PasswordTooShort), Description = $"Şifrə ən azı {length} simvol olmalıdır." };

    public override IdentityError PasswordRequiresDigit() =>
        new() { Code = nameof(PasswordRequiresDigit), Description = "Şifrədə ən azı bir rəqəm olmalıdır." };

    public override IdentityError PasswordRequiresLower() =>
        new() { Code = nameof(PasswordRequiresLower), Description = "Şifrədə ən azı bir kiçik hərf olmalıdır." };

    public override IdentityError PasswordRequiresUpper() =>
        new() { Code = nameof(PasswordRequiresUpper), Description = "Şifrədə ən azı bir böyük hərf olmalıdır." };

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        new() { Code = nameof(PasswordRequiresNonAlphanumeric), Description = "Şifrədə ən azı bir simvol olmalıdır." };

    public override IdentityError InvalidToken() =>
        new() { Code = nameof(InvalidToken), Description = "Keçid etibarsızdır və ya vaxtı bitib. Yenisini istəyin." };

    public override IdentityError PasswordMismatch() =>
        new() { Code = nameof(PasswordMismatch), Description = "Cari şifrə yanlışdır." };
}
