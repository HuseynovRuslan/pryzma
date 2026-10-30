using System.ComponentModel.DataAnnotations;

namespace Meydan.Api.Features.Auth;

public sealed record RegisterRequest
{
    /// <summary>The robot check's token (Cloudflare Turnstile), when the check is on.</summary>
    [StringLength(2048)]
    public string? CaptchaToken { get; init; }

    [Required(ErrorMessage = "Adınızı yazın.")]
    [StringLength(80, ErrorMessage = "Ad 80 simvoldan uzun ola bilməz.")]
    public string FirstName { get; init; } = "";

    [Required(ErrorMessage = "Soyadınızı yazın.")]
    [StringLength(80, ErrorMessage = "Soyad 80 simvoldan uzun ola bilməz.")]
    public string LastName { get; init; } = "";

    [Required(ErrorMessage = "Telefon nömrənizi yazın.")]
    [RegularExpression(@"^\+?[0-9 ()\-]{7,20}$", ErrorMessage = "Telefon nömrəsini rəqəmlərlə yazın, məsələn +994 50 123 45 67.")]
    public string Phone { get; init; } = "";

    [Required(ErrorMessage = "E-mail ünvanınızı yazın.")]
    [EmailAddress(ErrorMessage = "E-mail ünvanı düzgün deyil.")]
    [StringLength(256)]
    public string Email { get; init; } = "";

    [Required(ErrorMessage = "Şifrə yazın.")]
    [StringLength(128, MinimumLength = 8, ErrorMessage = "Şifrə ən azı 8 simvol olmalıdır.")]
    public string Password { get; init; } = "";

    [Required(ErrorMessage = "Şifrəni təkrar yazın.")]
    [Compare(nameof(Password), ErrorMessage = "Şifrələr üst-üstə düşmür.")]
    public string ConfirmPassword { get; init; } = "";

    [Range(typeof(bool), "true", "true", ErrorMessage = "Davam etmək üçün şərtlərlə razılaşın.")]
    public bool AcceptTerms { get; init; }
}

public sealed record LoginRequest
{
    [Required(ErrorMessage = "E-mail ünvanınızı yazın.")]
    [EmailAddress(ErrorMessage = "E-mail ünvanı düzgün deyil.")]
    public string Email { get; init; } = "";

    [Required(ErrorMessage = "Şifrənizi yazın.")]
    public string Password { get; init; } = "";

    public bool RememberMe { get; init; }
}

public sealed record ForgotPasswordRequest
{
    [Required(ErrorMessage = "E-mail ünvanınızı yazın.")]
    [EmailAddress(ErrorMessage = "E-mail ünvanı düzgün deyil.")]
    public string Email { get; init; } = "";
}

public sealed record ResetPasswordRequest
{
    [Required, EmailAddress(ErrorMessage = "E-mail ünvanı düzgün deyil.")]
    public string Email { get; init; } = "";

    [Required(ErrorMessage = "Keçid natamamdır. E-maildəki linki yenidən açın.")]
    public string Token { get; init; } = "";

    [Required(ErrorMessage = "Yeni şifrə yazın.")]
    [StringLength(128, MinimumLength = 8, ErrorMessage = "Şifrə ən azı 8 simvol olmalıdır.")]
    public string Password { get; init; } = "";

    [Compare(nameof(Password), ErrorMessage = "Şifrələr üst-üstə düşmür.")]
    public string ConfirmPassword { get; init; } = "";
}

public sealed record UserDto(
    Guid Id,
    string FirstName,
    string LastName,
    string Email,
    string? Phone,
    IReadOnlyList<string> Roles,
    bool EmailConfirmed = false);

public sealed record ConfirmEmailRequest
{
    [Required, EmailAddress(ErrorMessage = "E-mail ünvanı düzgün deyil.")]
    public string Email { get; init; } = "";

    [Required(ErrorMessage = "Keçid natamamdır. E-maildəki linki yenidən açın.")]
    public string Token { get; init; } = "";
}

public sealed record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, UserDto User);
