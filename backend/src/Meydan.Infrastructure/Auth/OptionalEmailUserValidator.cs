using Meydan.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Meydan.Infrastructure.Auth;

/// <summary>
/// Identity's default validator refuses an account without an e-mail address once RequireUniqueEmail is on. A
/// QRLog employee may have no address at all (they sign in to QRLog by phone), and such a person still gets a
/// MEYDAN account through "QRLog ilə daxil ol". This keeps every rule for accounts that HAVE an address — it must
/// be well-formed and unique — and for the rest checks only what the default checks about the user name.
/// </summary>
public sealed class OptionalEmailUserValidator(IdentityErrorDescriber? errors = null) : UserValidator<ApplicationUser>(errors)
{
    public override async Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.Email))
            return await base.ValidateAsync(manager, user);

        var errors = new List<IdentityError>();
        var userName = await manager.GetUserNameAsync(user);
        if (string.IsNullOrWhiteSpace(userName))
            errors.Add(Describer.InvalidUserName(userName));
        else if (!string.IsNullOrEmpty(manager.Options.User.AllowedUserNameCharacters)
                 && userName.Any(c => !manager.Options.User.AllowedUserNameCharacters.Contains(c)))
            errors.Add(Describer.InvalidUserName(userName));
        else
        {
            var owner = await manager.FindByNameAsync(userName);
            if (owner is not null && !string.Equals(await manager.GetUserIdAsync(owner), await manager.GetUserIdAsync(user), StringComparison.Ordinal))
                errors.Add(Describer.DuplicateUserName(userName));
        }
        return errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed(errors.ToArray());
    }
}
