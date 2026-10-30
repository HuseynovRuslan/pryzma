using Meydan.Api.Common;
using Meydan.Infrastructure.Files;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Meydan.Api.Features.Admin;

/// <summary>
/// Public images uploaded by admins (project covers). Only keys inside the "covers" storage area are served —
/// submission files can never be reached through this endpoint.
/// </summary>
[ApiController]
[Route("api/media")]
public sealed class MediaController(IFileStorage storage) : ControllerBase
{
    [HttpGet("{**key}")]
    public IActionResult Get(string key)
    {
        if (!StorageAreas.IsIn(key, StorageAreas.Covers))
            throw new NotFoundException();

        var type = FileTypes.ByExtension(Path.GetExtension(key));
        if (type is null || type == FileTypes.Pdf)
            throw new NotFoundException();

        Stream content;
        try
        {
            content = storage.OpenRead(key);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            throw new NotFoundException();
        }

        // Keys are random and never reused, so the image can be cached for a long time.
        Response.Headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        return File(content, type.MimeType);
    }
}
