using System.ComponentModel.DataAnnotations;
using Meydan.Api.Common;
using Meydan.Api.Features.Auth;
using Meydan.Api.Features.Auth.Qrlog;
using Meydan.Api.Features.Catalog;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Me;

public sealed record MySubmissionDto(
    int Id,
    string? ReferenceNumber,
    string Title,
    SubmissionStatus Status,
    bool IsEditable,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ProjectName,
    string ProjectSlug,
    string CompetitionTitle,
    string CompetitionSlug,
    string? CategoryLabel,
    string? CategoryName,
    /// <summary>The competition's results are public; only then is <see cref="IsWinner"/> meaningful.</summary>
    bool ResultsPublished,
    bool IsWinner,
    /// <summary>1, 2 or 3 when the work holds a place of a competition without categories (and results are public).</summary>
    int? Place);

public sealed record UpdateProfileRequest
{
    [Required(ErrorMessage = "Adınızı yazın.")]
    [StringLength(80, ErrorMessage = "Ad 80 simvoldan uzun ola bilməz.")]
    public string FirstName { get; init; } = "";

    [Required(ErrorMessage = "Soyadınızı yazın.")]
    [StringLength(80, ErrorMessage = "Soyad 80 simvoldan uzun ola bilməz.")]
    public string LastName { get; init; } = "";

    [Required(ErrorMessage = "Telefon nömrənizi yazın.")]
    [RegularExpression(@"^\+?[0-9 ()\-]{7,20}$", ErrorMessage = "Telefon nömrəsini rəqəmlərlə yazın, məsələn +994 50 123 45 67.")]
    public string Phone { get; init; } = "";
}

[ApiController]
[Authorize]
[Route("api/me")]
public sealed class MeController(
    MeydanDbContext db,
    UserManager<ApplicationUser> users,
    AuditTrail audit,
    TimeProvider clock) : ControllerBase
{
    /// <summary>"İşlərim": every draft and submission of the signed-in user, across all projects.</summary>
    [HttpGet("submissions")]
    public async Task<IReadOnlyList<MySubmissionDto>> Submissions(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var now = clock.GetUtcNow();
        var rows = await db.Submissions.AsNoTracking()
            .Where(s => s.UserId == userId && s.Status != SubmissionStatus.Withdrawn)
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new MySubmissionDto(
                s.Id, s.ReferenceNumber, s.Title, s.Status, s.Status == SubmissionStatus.Draft,
                s.SubmittedAt, s.CreatedAt, s.UpdatedAt,
                s.Competition.Project.Name, s.Competition.Project.Slug,
                s.Competition.Title, s.Competition.Slug,
                s.Competition.CategoryLabel,
                s.Category != null ? s.Category.Name : null,
                false, false, null))
            .ToListAsync(ct);

        // A participant learns about results only once they are public, exactly like everybody else.
        var ids = rows.Select(r => r.Id).ToList();
        var results = await db.Submissions.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && s.Competition.Result != null && s.Competition.Result.PublishedAt != null)
            .Select(s => new
            {
                s.Id, s.Competition, s.Competition.Result!.PublishedAt,
                IsWinner = s.CategoryWin != null || s.Competition.Result.OverallWinnerSubmissionId == s.Id
                           || s.Competition.Result.SecondPlaceSubmissionId == s.Id
                           || s.Competition.Result.ThirdPlaceSubmissionId == s.Id,
                Place = s.Competition.HasCategories ? (int?)null
                    : s.Competition.Result.OverallWinnerSubmissionId == s.Id ? 1
                    : s.Competition.Result.SecondPlaceSubmissionId == s.Id ? 2
                    : s.Competition.Result.ThirdPlaceSubmissionId == s.Id ? 3
                    : null,
            })
            .ToListAsync(ct);
        return rows.Select(r =>
        {
            var x = results.FirstOrDefault(v => v.Id == r.Id);
            if (x is null || !CompetitionPhases.AreResultsPublic(x.Competition, x.PublishedAt, now)) return r;
            return r with { ResultsPublished = true, IsWinner = x.IsWinner, Place = x.Place };
        }).ToList();
    }

    [HttpGet("profile")]
    public async Task<UserDto> Profile()
    {
        var user = await FindUserAsync();
        return AuthService.ToDto(user, await users.GetRolesAsync(user));
    }

    [HttpPut("profile")]
    public async Task<UserDto> UpdateProfile(UpdateProfileRequest req)
    {
        var user = await FindUserAsync();
        // A number this account already has may stay as it is, however it was written then; a changed number must be a
        // plausible Azerbaijani mobile that no other account holds (D12).
        if (PhoneNumbers.Digits(req.Phone) != PhoneNumbers.Digits(user.PhoneNumber))
        {
            if (PhoneNumbers.AzMobile(req.Phone) is null)
                throw ValidationFailedException.For("phone", PhoneNumbers.NotAMobile);
            if (await PhoneNumbers.InUseAsync(users.Users, req.Phone, except: user.Id, HttpContext.RequestAborted))
                throw ValidationFailedException.For("phone", PhoneNumbers.AlreadyUsed);
        }
        user.FirstName = req.FirstName.Trim();
        user.LastName = req.LastName.Trim();
        user.PhoneNumber = req.Phone.Trim();
        user.UpdatedAt = clock.GetUtcNow();
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
            throw new ValidationFailedException(new Dictionary<string, string[]>
            {
                [""] = result.Errors.Select(e => e.Description).ToArray(),
            });
        return AuthService.ToDto(user, await users.GetRolesAsync(user));
    }

    /// <summary>Sign-in methods attached to this account, and whether a password is set (an account made through QRLog has none until reset).</summary>
    [HttpGet("logins")]
    public async Task<LoginsDto> Logins()
    {
        var user = await FindUserAsync();
        var logins = await users.GetLoginsAsync(user);
        return new LoginsDto(
            await users.HasPasswordAsync(user),
            logins.Any(l => l.LoginProvider == QrlogSsoOptions.Provider));
    }

    /// <summary>
    /// Detaches the QRLog identity. Refused while the account has no password: it would have no way in left.
    /// Audited under the account itself.
    /// </summary>
    [HttpDelete("logins/qrlog")]
    public async Task<IActionResult> UnlinkQrlog(CancellationToken ct)
    {
        var user = await FindUserAsync();
        var login = (await users.GetLoginsAsync(user)).FirstOrDefault(l => l.LoginProvider == QrlogSsoOptions.Provider);
        if (login is null)
            return NoContent();
        if (!await users.HasPasswordAsync(user))
            throw new ConflictException("Əvvəlcə hesab üçün şifrə təyin edin (“Şifrəni unutmuşam” ilə), sonra QRLog bağlantısını ayırın.");

        var result = await users.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
        if (!result.Succeeded)
            throw new ConflictException("QRLog bağlantısı ayrılmadı. Yenidən cəhd edin.");
        audit.Record(user.Id, AuditActions.QrlogUnlinked, AuditEntities.Account, user.Id);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<ApplicationUser> FindUserAsync() =>
        await users.FindByIdAsync(User.GetUserId().ToString())
        ?? throw new NotFoundException("Hesab tapılmadı.");
}

public sealed record LoginsDto(bool HasPassword, bool Qrlog);
