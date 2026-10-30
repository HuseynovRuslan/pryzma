using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meydan.Infrastructure.Auth;

/// <summary>Where Identity looks for the e-mail confirmation token provider.</summary>
public static class EmailConfirmationTokenProvider
{
    public const string Name = "EmailConfirmation";
}

/// <summary>
/// Options of their own, so a confirmation link can outlive the short password-reset token, and a token of one kind
/// is never accepted as the other (the provider name is part of the protector's purpose).
/// </summary>
public sealed class EmailConfirmationTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public EmailConfirmationTokenProviderOptions()
    {
        Name = EmailConfirmationTokenProvider.Name;
        TokenLifespan = TimeSpan.FromHours(48);
    }
}

/// <summary>
/// The e-mail confirmation token: signed with the same key ring in PostgreSQL as the password reset, bound to the
/// account's security stamp, valid for <see cref="DependencyInjection.EmailConfirmationLifetime"/>.
/// </summary>
public sealed class EmailConfirmationTokenProvider<TUser>(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<EmailConfirmationTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<TUser>> logger)
    : DataProtectorTokenProvider<TUser>(dataProtectionProvider, options, logger)
    where TUser : class;
