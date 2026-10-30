using Meydan.Infrastructure.Files;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Common;

/// <summary>
/// Raises the request body limit for one upload endpoint to exactly one file of the configured maximum size plus
/// <see cref="MultipartOverheadBytes"/> for boundaries and part headers (a real 10 MB file needs ~300 bytes of it).
/// Runs as a resource filter, i.e. before model binding. Every other endpoint keeps the small global limit
/// (Kestrel:MaxRequestBodyBytes), so large bodies are refused unread. Upload endpoints accept one file per request.
///
/// It also keeps MVC from reading the multipart body while binding: the action reads it itself through
/// <see cref="UploadForms.ReadAsync"/>, so a body that is too large or malformed gets the upload's own Azerbaijani
/// answer instead of a framework message.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UploadBodyLimitAttribute : Attribute, IResourceFilter
{
    public const long MultipartOverheadBytes = 1024 * 1024;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var uploads = context.HttpContext.RequestServices.GetRequiredService<IOptions<UploadOptions>>().Value;
        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = uploads.MaxFileSizeBytes + MultipartOverheadBytes;

        context.ValueProviderFactories.RemoveType<FormValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<FormFileValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}

public static class UploadForms
{
    /// <summary>
    /// Reads an upload request's form and returns its single file. Every failure is a field error under
    /// <paramref name="field"/>: not multipart, body over the limit, malformed body, no file, more than one file.
    /// </summary>
    public static async Task<(IFormCollection Form, IFormFile File)> ReadSingleFileAsync(
        HttpRequest request, string field, UploadOptions policy, CancellationToken ct)
    {
        if (!request.HasFormContentType)
            throw ValidationFailedException.For(field, "Fayl multipart/form-data kimi göndərilməlidir.");

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) // BadHttpRequestException is an IOException
        {
            throw ValidationFailedException.For(field, IsTooLarge(ex)
                ? $"Fayl çox böyükdür. Hər fayl ən çox {policy.MaxFileSizeMb} MB ola bilər."
                : "Göndərilən fayl oxunmadı. Yenidən cəhd edin.");
        }

        return form.Files.Count switch
        {
            0 => throw ValidationFailedException.For(field, "Fayl seçilməyib."),
            1 => (form, form.Files[0]),
            _ => throw ValidationFailedException.For(field, "Hər sorğuda yalnız bir fayl göndərin."),
        };
    }

    /// <summary>Kestrel's body limit (413) or a multipart limit ("… limit … exceeded"), as opposed to a broken body.</summary>
    private static bool IsTooLarge(Exception ex) =>
        ex is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
        || (ex is InvalidDataException && ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase));
}
