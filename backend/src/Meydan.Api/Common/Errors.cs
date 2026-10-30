using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Meydan.Api.Common;

/// <summary>Expected failures that map to a specific HTTP status and a user-facing message.</summary>
public class AppException(int status, string title, string? detail = null) : Exception(title)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
}

public sealed class NotFoundException(string title = "Axtardığınız məlumat tapılmadı.")
    : AppException(StatusCodes.Status404NotFound, title);

public sealed class ConflictException(string title) : AppException(StatusCodes.Status409Conflict, title);

/// <summary>Not signed in (or the sign-in failed) — e.g. a juror's invitation or session.</summary>
public sealed class UnauthorizedException(string title) : AppException(StatusCodes.Status401Unauthorized, title);

/// <summary>Signed in, but not allowed to do this — never used to say that something does not exist.</summary>
public sealed class ForbiddenException(string title) : AppException(StatusCodes.Status403Forbidden, title);

/// <summary>
/// The request is well formed and the thing it names exists, but the state it is in does not allow it — a vote
/// after the window has shut, for instance. Distinct from a 409, which means someone else got there first.
/// </summary>
public sealed class UnprocessableException(string title) : AppException(StatusCodes.Status422UnprocessableEntity, title);

/// <summary>Validation failure with per-field messages, rendered as ValidationProblemDetails.</summary>
public sealed class ValidationFailedException(IDictionary<string, string[]> errors)
    : AppException(StatusCodes.Status400BadRequest, "Formada düzəliş tələb olunur.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;

    public static ValidationFailedException For(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>Turns every unhandled exception into RFC 7807 problem details; hides internals on 500.</summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem;
        if (exception is ValidationFailedException invalid)
        {
            problem = new ValidationProblemDetails(invalid.Errors) { Status = invalid.Status, Title = invalid.Title };
        }
        else if (exception is AppException app)
        {
            problem = new ProblemDetails { Status = app.Status, Title = app.Title, Detail = app.Detail };
        }
        else if (exception is BadHttpRequestException bad)
        {
            // Malformed or oversized request (e.g. body above Kestrel:MaxRequestBodyBytes → 413): the client's fault,
            // not a server error. The title is filled in Azerbaijani from the status code.
            problem = new ProblemDetails { Status = bad.StatusCode };
        }
        else if (exception is OperationCanceledException && http.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing useful to send.
            return true;
        }
        else
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path} ({CorrelationId})",
                http.Request.Method, http.Request.Path, http.TraceIdentifier);
            // Never the exception text, stack, SQL or paths: only a reference the user can quote to support.
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Gözlənilməz xəta baş verdi. Bir az sonra yenidən cəhd edin.",
                Detail = $"Problem təkrarlanarsa, bu istinadı bildirin: {http.TraceIdentifier}",
            };
        }

        http.Response.StatusCode = problem.Status!.Value;
        http.Response.Headers[RequestLoggingMiddleware.Header] = http.TraceIdentifier; // cleared with the failed response
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
