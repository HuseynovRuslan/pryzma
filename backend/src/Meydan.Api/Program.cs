using System.Text.Json.Serialization;
using Meydan.Api.Common;
using Meydan.Api.Features.Auth;
using Meydan.Api.Features.Catalog;
using Meydan.Api.Features.Submissions;
using Meydan.Infrastructure;
using Meydan.Infrastructure.Auth;
using Meydan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Production/Staging refuse to start with missing or unsafe settings (names only, never values).
ProductionConfiguration.Validate(builder.Configuration, builder.Environment);

// Small global body limit; upload endpoints raise it for themselves ([UploadBodyLimit]).
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = builder.Configuration.GetValue("Kestrel:MaxRequestBodyBytes", 2L * 1024 * 1024);
    k.AddServerHeader = false;
});

// Behind a reverse proxy (Nginx) the client IP and scheme come from X-Forwarded-*; they are trusted only from
// loopback and the proxies/networks named in configuration — never from arbitrary clients.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        if (System.Net.IPAddress.TryParse(proxy, out var ip)) o.KnownProxies.Add(ip);
    foreach (var network in builder.Configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
        if (ProductionConfiguration.TryParseNetwork(network, out var n)) o.KnownNetworks.Add(n!);
});

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.Configure<AuthCookieOptions>(builder.Configuration.GetSection(AuthCookieOptions.Section));
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<Meydan.Api.Features.Auth.EmailConfirmationPolicy>();
builder.Services.AddSingleton<Meydan.Api.Features.Auth.EmailFirstRegistrationPolicy>();
builder.Services.AddScoped<Meydan.Api.Features.Auth.EmailFirstRegistration>();
builder.Services.Configure<Meydan.Api.Features.Auth.CaptchaOptions>(builder.Configuration.GetSection(Meydan.Api.Features.Auth.CaptchaOptions.Section));
builder.Services.AddHttpClient<Meydan.Api.Features.Auth.ICaptchaVerifier, Meydan.Api.Features.Auth.TurnstileVerifier>();
builder.Services.Configure<Meydan.Api.Features.Contact.ContactOptions>(builder.Configuration.GetSection(Meydan.Api.Features.Contact.ContactOptions.Section));
builder.Services.AddScoped<Meydan.Api.Features.Contact.ContactMessages>();
builder.Services.AddScoped<CatalogQueries>();
builder.Services.AddScoped<SubmissionService>();
builder.Services.AddScoped<Meydan.Api.Features.Results.PublicResults>();
builder.Services.AddScoped<Meydan.Api.Features.Voting.PublicVoting>();
builder.Services.AddScoped<Meydan.Api.Features.Admin.VoteReview>();
builder.Services.AddScoped<Meydan.Api.Features.Admin.VoteCatalogAnalytics>();
// Rendered PDF pages for the ballot, kept in memory: bounded to 128 MB (entries are sized in bytes).
builder.Services.AddMemoryCache(o => o.SizeLimit = 128L * 1024 * 1024);
builder.Services.AddSingleton<Meydan.Api.Features.Voting.DocumentPages>();
builder.Services.AddScoped<Meydan.Api.Features.Admin.ResultAdministration>();
builder.Services.AddScoped<Meydan.Api.Features.Admin.AwardAdministration>();
builder.Services.AddScoped<Meydan.Api.Features.Final.FinalAdministration>();
builder.Services.AddScoped<Meydan.Api.Features.Jury.JuryAccess>();
builder.Services.AddScoped<Meydan.Api.Features.Jury.JuryEvaluationService>();
builder.Services.AddScoped<Meydan.Api.Features.Jury.JuryFiles>();
builder.Services.AddScoped<Meydan.Api.Features.Admin.PrintItems>();
builder.Services.AddScoped<Meydan.Api.Features.Admin.PermanentDeletion>();
builder.Services.AddScoped<Meydan.Api.Features.Admin.VotingLaunch>();
builder.Services.AddScoped<Meydan.Api.Features.Admin.SubmissionMove>();
builder.Services.AddScoped<Meydan.Api.Features.Platform.PlatformSettingsStore>();
builder.Services.AddScoped<Meydan.Infrastructure.Email.IEmailComposer, Meydan.Api.Features.Email.EmailComposer>();
builder.Services.Configure<Meydan.Api.Features.Auth.Qrlog.QrlogSsoOptions>(builder.Configuration.GetSection(Meydan.Api.Features.Auth.Qrlog.QrlogSsoOptions.Section));
builder.Services.AddScoped<Meydan.Api.Features.Auth.Qrlog.QrlogSsoService>();
builder.Services.AddScoped<Meydan.Api.Features.Demo.DemoData>();

builder.Services
    .AddControllers(o =>
    {
        // Validation errors keyed by JSON (camelCase) names so the SPA can place them on fields.
        o.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider());
    })
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .ConfigureApiBehaviorOptions(o =>
    {
        o.InvalidModelStateResponseFactory = ctx =>
        {
            // MVC reads a form body while binding; if that fails, the framework's English exception text lands in
            // ModelState. Never echo it: answer like any other unreadable or oversized request.
            var formFailure = ctx.ModelState.Values.SelectMany(v => v.Errors)
                .FirstOrDefault(e => e.ErrorMessage.StartsWith("Failed to read the request form", StringComparison.Ordinal));
            if (formFailure is not null)
            {
                var tooLarge = formFailure.ErrorMessage.Contains("too large", StringComparison.OrdinalIgnoreCase)
                               || formFailure.ErrorMessage.Contains("limit", StringComparison.OrdinalIgnoreCase);
                var status = tooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest;
                var failure = ctx.HttpContext.RequestServices
                    .GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.ProblemDetailsFactory>()
                    .CreateProblemDetails(ctx.HttpContext, status, tooLarge ? "Sorğu çox böyükdür." : "Sorğu oxunmadı.");
                return new ObjectResult(failure) { StatusCode = status, ContentTypes = { "application/problem+json" } };
            }

            var problem = new ValidationProblemDetails(ctx.ModelState)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Formada düzəliş tələb olunur.",
            };
            return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
        };
    });

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDetailsSetup.Customize);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddMeydanRateLimiting(builder.Configuration);

var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (jwt.SigningKey.Length < 32)
    throw new InvalidOperationException(
        "Jwt:SigningKey is missing or shorter than 32 characters. It is never stored in appsettings: " +
        "development → `dotnet user-secrets set \"Jwt:SigningKey\" \"<random>\" --project src/Meydan.Api`, " +
        "other environments → environment variable Jwt__SigningKey or the host's secret store.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = AccessTokenService.ValidationParameters(jwt);
        // A password reset ends already-issued access tokens too, not only refresh tokens.
        o.Events = new JwtBearerEvents { OnTokenValidated = SessionStampCheck.ValidateAsync };
    });
builder.Services.AddAuthorization();

// The SPA is normally served from the same origin (proxy), so no CORS is needed. Cross-origin clients must be
// listed exactly; credentials are only ever allowed for that explicit list — never with a wildcard.
var corsOrigins = (builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .Where(origin => !string.IsNullOrWhiteSpace(origin) && !origin.Contains('*')).ToArray();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins)
        .WithMethods("GET", "POST", "PUT", "DELETE")
        .WithHeaders("Content-Type", "Authorization", RequestLoggingMiddleware.Header)
        .AllowCredentials()
        .SetPreflightMaxAge(TimeSpan.FromHours(1))));

builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = false; // opt in once every subdomain is HTTPS-only
});

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag])
    .AddCheck<StorageHealthCheck>("storage", tags: [HealthEndpoints.ReadyTag]);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts(); // sent only on HTTPS requests (as seen through the trusted proxy)
}

app.UseCors();
app.UseAuthentication();
// After authentication, so participant limits are counted per user rather than per shared IP.
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapMeydanHealth();

// `Meydan.Api migrate` is the production deployment step: apply migrations + seed, then exit.
if (args.Contains("migrate", StringComparer.OrdinalIgnoreCase))
{
    await DatabaseStartup.MigrateAndSeedAsync(app.Services);
    return;
}

// `Meydan.Api seed-demo` / `remove-demo`: demo content, explicit and never at startup (see DemoCommands).
if (Meydan.Api.Features.Demo.DemoCommands.IsRequested(args))
{
    Environment.ExitCode = await Meydan.Api.Features.Demo.DemoCommands.RunAsync(args, app.Services, app.Environment);
    return;
}

await DatabaseStartup.InitializeAsync(app);

app.Run();

public partial class Program;
