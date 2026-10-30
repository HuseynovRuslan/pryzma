using System.Net;
using Meydan.Infrastructure.Email;
using Meydan.Infrastructure.Files;

namespace Meydan.Api.Common;

/// <summary>
/// Fail-fast check of the settings a public deployment cannot run without. Runs for the Production and Staging
/// environments before anything else starts. Messages name the setting (and its environment-variable form) —
/// never a value, so no secret can leak into a startup log.
/// </summary>
public static class ProductionConfiguration
{
    public static bool Applies(IHostEnvironment env) => env.IsProduction() || env.IsStaging();

    public static void Validate(IConfiguration config, IHostEnvironment env)
    {
        if (!Applies(env)) return;
        var problems = Check(config, env.ContentRootPath);
        if (problems.Count == 0) return;
        throw new InvalidOperationException(
            "Production configuration is incomplete or unsafe:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems.Select(p => "  - " + p)) + Environment.NewLine +
            "Set these through environment variables or the host's secret store (see README, \"Configuration\").");
    }

    public static List<string> Check(IConfiguration config, string contentRoot)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(config.GetConnectionString("Meydan")))
            problems.Add("ConnectionStrings:Meydan is not set (ConnectionStrings__Meydan).");

        if ((config["Jwt:SigningKey"] ?? "").Length < 32)
            problems.Add("Jwt:SigningKey is missing or shorter than 32 characters (Jwt__SigningKey).");

        if (!IsHttpsUrl(config["App:PublicUrl"]))
            problems.Add("App:PublicUrl must be the absolute https:// address of the site, e.g. https://meydan.az (App__PublicUrl).");

        if (!config.GetValue("AuthCookie:Secure", true))
            problems.Add("AuthCookie:Secure must be true in production (refresh cookie over HTTPS only).");

        var storage = config["Uploads:StorageRoot"];
        if (string.IsNullOrWhiteSpace(storage) || !Path.IsPathRooted(storage))
            problems.Add("Uploads:StorageRoot must be an absolute path on a persistent volume outside the application directory (Uploads__StorageRoot).");
        else
        {
            var root = LocalFileStorage.ResolveRoot(storage, contentRoot) + Path.DirectorySeparatorChar;
            static string Dir(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (root.StartsWith(Dir(contentRoot), StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(Dir(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                problems.Add("Uploads:StorageRoot must not be inside the application or deployment directory (a redeploy would delete uploads).");
        }

        foreach (var origin in config.GetSection("Cors:Origins").Get<string[]>() ?? [])
            if (origin.Contains('*') || !IsHttpsUrl(origin) || new Uri(origin).AbsolutePath != "/")
                problems.Add("Cors:Origins entries must be exact https:// origins without path or wildcard (Cors__Origins__0, …).");

        foreach (var proxy in config.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
            if (!IPAddress.TryParse(proxy, out _))
                problems.Add("ReverseProxy:KnownProxies entries must be IP addresses.");
        foreach (var network in config.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
            if (!TryParseNetwork(network, out _))
                problems.Add("ReverseProxy:KnownNetworks entries must be CIDR ranges, e.g. 10.0.0.0/8.");

        if (config.GetValue("QrlogSso:Enabled", false))
        {
            if ((config["QrlogSso:VouchSecret"] ?? "").Length < 32)
                problems.Add("QrlogSso:VouchSecret is missing or shorter than 32 characters (QrlogSso__VouchSecret) — required while QrlogSso:Enabled is true.");
            if (!Meydan.Api.Features.Auth.Qrlog.QrlogSsoOptions.IsValidAuthorizeUrl(config["QrlogSso:AuthorizeUrl"]))
                problems.Add("QrlogSso:AuthorizeUrl must be an https address on qrlog.az containing {code} and {return} (QrlogSso__AuthorizeUrl).");
        }

        var email = config.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();
        switch (email.Mode)
        {
            case null:
                problems.Add("Email:Mode must be set explicitly: Smtp (password reset e-mails work) or Disabled (Email__Mode).");
                break;
            case EmailMode.Smtp:
                if (string.IsNullOrWhiteSpace(email.Smtp.Host)) problems.Add("Email:Smtp:Host is not set (Email__Smtp__Host).");
                if (email.Smtp.Port is <= 0 or > 65535) problems.Add("Email:Smtp:Port is not a valid port (Email__Smtp__Port).");
                if (string.IsNullOrWhiteSpace(email.FromAddress) || !email.FromAddress.Contains('@'))
                    problems.Add("Email:FromAddress must be a valid sender address (Email__FromAddress).");
                if (!string.IsNullOrEmpty(email.Smtp.Username) && string.IsNullOrEmpty(email.Smtp.Password))
                    problems.Add("Email:Smtp:Password is required when Email:Smtp:Username is set (Email__Smtp__Password).");
                break;
            case EmailMode.Log:
            case EmailMode.Pickup:
                problems.Add($"Email:Mode={email.Mode} writes message contents (reset links) to disk/logs and is not allowed in production.");
                break;
        }
        return problems;
    }

    public static bool TryParseNetwork(string value, out Microsoft.AspNetCore.HttpOverrides.IPNetwork? network)
    {
        network = null;
        var parts = value.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var prefix) || !int.TryParse(parts[1], out var length))
            return false;
        var max = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (length < 0 || length > max) return false;
        network = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length);
        return true;
    }

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Query);
}
