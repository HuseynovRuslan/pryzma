using System.ComponentModel.DataAnnotations;
using Meydan.Api.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Meydan.Api.Features.Contact;

[ApiController]
[Route("api/contact")]
[EnableRateLimiting(RateLimitPolicies.Contact)]
public sealed class ContactController(ContactMessages messages) : ControllerBase
{
    /// <summary>The site's contact form: one message to the organiser, by e-mail (see <see cref="ContactMessages"/>).</summary>
    [HttpPost]
    public async Task<IActionResult> Send(ContactRequest req, CancellationToken ct)
    {
        await messages.SendAsync(req, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return NoContent();
    }
}

public sealed record ContactRequest
{
    /// <summary>The robot check's token (Cloudflare Turnstile), when the check is on.</summary>
    [StringLength(2048)]
    public string? CaptchaToken { get; init; }

    [Required(ErrorMessage = "Adınızı yazın.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Ad 2–100 simvol olmalıdır.")]
    public string Name { get; init; } = "";

    [Required(ErrorMessage = "E-poçt ünvanınızı yazın.")]
    [EmailAddress(ErrorMessage = "E-poçt ünvanı düzgün deyil.")]
    [StringLength(256, ErrorMessage = "E-poçt ünvanı çox uzundur.")]
    public string Email { get; init; } = "";

    [Required(ErrorMessage = "Mesajınızı yazın.")]
    [StringLength(4000, MinimumLength = 10, ErrorMessage = "Mesaj 10–4000 simvol olmalıdır.")]
    public string Message { get; init; } = "";
}
