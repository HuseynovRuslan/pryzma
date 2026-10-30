using Meydan.Domain.Entities;
using Meydan.Infrastructure.Auth;
using Meydan.Infrastructure.Email;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Meydan.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Auth:PasswordResetTokenLifetime (hh:mm:ss, default one hour, at most one day).</summary>
    /// <summary>How long an e-mail confirmation link works: 48 hours unless configured, between 1 hour and 7 days.</summary>
    public static TimeSpan EmailConfirmationLifetime(IConfiguration config)
    {
        var value = config.GetValue("Auth:EmailConfirmationTokenLifetime", TimeSpan.FromHours(48));
        return value < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : value > TimeSpan.FromDays(7) ? TimeSpan.FromDays(7) : value;
    }

    public static TimeSpan PasswordResetLifetime(IConfiguration config)
    {
        var value = config.GetValue("Auth:PasswordResetTokenLifetime", TimeSpan.FromHours(1));
        return value <= TimeSpan.Zero ? TimeSpan.FromHours(1) : value > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : value;
    }

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        services.AddSingleton(TimeProvider.System);

        // PostgreSQL is the only supported database. The connection string (with its password) comes from
        // user-secrets in development and from the environment / secret store elsewhere — never appsettings.
        var connectionString = config.GetConnectionString("Meydan");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:Meydan is not configured. Development: run scripts/dev-postgres.ps1 (or set it with " +
                "`dotnet user-secrets set \"ConnectionStrings:Meydan\" \"Host=…;Database=…;Username=…;Password=…\"`); " +
                "other environments: environment variable ConnectionStrings__Meydan.");
        services.AddDbContext<MeydanDbContext>(o =>
            o.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3)));

        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
                o.Tokens.EmailConfirmationTokenProvider = EmailConfirmationTokenProvider.Name;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<MeydanDbContext>()
            .AddErrorDescriber<AzerbaijaniIdentityErrorDescriber>()
            .AddDefaultTokenProviders()
            .AddTokenProvider<EmailConfirmationTokenProvider<ApplicationUser>>(EmailConfirmationTokenProvider.Name);
        // Accounts created from a QRLog identity may have no e-mail address; everything else Identity checks stays.
        services.RemoveAll<IUserValidator<ApplicationUser>>();
        services.AddScoped<IUserValidator<ApplicationUser>, OptionalEmailUserValidator>();

        // Password-reset tokens: short-lived, and signed with a key ring kept in PostgreSQL so they survive restarts,
        // work across instances and are covered by the database backup.
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = PasswordResetLifetime(config));
        services.Configure<EmailConfirmationTokenProviderOptions>(o => o.TokenLifespan = EmailConfirmationLifetime(config));
        services.AddDataProtection()
            .SetApplicationName("MEYDAN")
            .PersistKeysToDbContext<MeydanDbContext>();

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.Configure<SeedOptions>(config.GetSection(SeedOptions.Section));
        services.Configure<UploadOptions>(config.GetSection(UploadOptions.Section));
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton(sp => new PublicDerivatives(sp.GetRequiredService<IOptions<UploadOptions>>().Value));
        // No antivirus is configured by default; the upload pipeline records "not scanned" rather than pretending.
        services.AddSingleton<IFileScanner, NoFileScanner>();
        services.AddSingleton<PublicAssetProcessor>();
        services.AddSingleton<PublicAssetWorker>();
        services.AddSingleton<IPublicAssetQueue>(sp => sp.GetRequiredService<PublicAssetWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<PublicAssetWorker>());

        services.AddScoped<AccessTokenService>();
        services.AddScoped<RefreshTokenService>();
        services.AddScoped<ReferenceNumberGenerator>();
        services.AddScoped<AuditTrail>();
        services.AddScoped<DbSeeder>();
        // E-mail: provider chosen by configuration; messages go through a durable PostgreSQL outbox and are delivered
        // in the background (see EmailOutboxWorker). The API layer supplies IEmailComposer (templates).
        // Mail bodies contain password-reset tokens: only a local Development host may log them, and the
        // .eml pickup directory is refused in Production by ProductionConfiguration.
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.Section));
        var mode = (config.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions()).EffectiveMode(environment);
        switch (mode)
        {
            case EmailMode.Smtp: services.AddSingleton<IEmailSender, SmtpEmailSender>(); break;
            case EmailMode.Pickup when !environment.IsProduction(): services.AddSingleton<IEmailSender, PickupDirectoryEmailSender>(); break;
            case EmailMode.Log when environment.IsDevelopment(): services.AddSingleton<IEmailSender, LogEmailSender>(); break;
            default: services.AddSingleton<IEmailSender, DisabledEmailSender>(); break;
        }
        services.AddSingleton<EmailOutboxSignal>();
        services.AddScoped<IEmailOutbox, EmailOutbox>();
        services.AddHostedService<EmailOutboxWorker>();

        return services;
    }
}
