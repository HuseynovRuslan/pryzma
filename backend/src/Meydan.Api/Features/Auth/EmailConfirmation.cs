using System.Globalization;
using Meydan.Api.Features.Email;
using Meydan.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Auth;

/// <summary>
/// Whether an account must confirm its e-mail address before it may vote. Off unless
/// <c>Auth:EmailConfirmation:RequiredForAccountsFrom</c> is set: from that moment on, a newly opened account is sent a
/// confirmation link and votes only once it has opened it. Accounts opened earlier are never asked — nobody already
/// signed in is locked out of anything, and no vote already cast is touched.
/// <para>
/// It needs mail that is actually sent. With <c>Email:Mode</c> Disabled the requirement stays off whatever the date
/// says: asking for a link that can never arrive would only stop honest people from voting.
/// </para>
/// </summary>
public sealed class EmailConfirmationPolicy(IConfiguration config, IOptions<EmailOptions> email, IHostEnvironment env)
{
    public const string NotConfirmed =
        "Səs vermək üçün e-mail ünvanınızı təsdiqləyin: ünvanınıza göndərilən məktubdakı linki açın. " +
        "Məktub gəlməyibsə, yenisini istəyin.";

    /// <summary>
    /// Accounts opened at or after this moment must confirm; null = nobody must. An empty or unreadable value (the
    /// container passes "" when the setting is unset) is null, never a startup failure.
    /// </summary>
    public DateTimeOffset? RequiredFrom { get; } =
        DateTimeOffset.TryParse(config["Auth:EmailConfirmation:RequiredForAccountsFrom"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var from) ? from : null;

    /// <summary>Mail leaves the server (SMTP, or a log or pickup directory outside production).</summary>
    public bool CanSend { get; } = email.Value.EffectiveMode(env) != EmailMode.Disabled;

    public bool Active => RequiredFrom is not null && CanSend;

    public bool Requires(DateTimeOffset accountCreatedAt, bool emailConfirmed) =>
        Active && !emailConfirmed && accountCreatedAt >= RequiredFrom;

    /// <summary>A new link no sooner than this after the last one.</summary>
    public static readonly TimeSpan ResendGap = TimeSpan.FromMinutes(1);

    /// <summary>At most this many links per account in 24 hours.</summary>
    public const int ResendsPerDay = 5;

    public const string Kind = EmailTemplates.EmailConfirmationKind;
}
