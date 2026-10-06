using Commercial.Application.Errors;
using Microsoft.AspNetCore.Diagnostics;

namespace Commercial.Api.Errors;

/// <summary>
/// Maps exceptions to the error envelope. Only <see cref="CommercialException"/> messages are
/// client-visible; every other exception becomes a generic 500 with no message or stack trace
/// (in every environment — details go to logs/traces only).
/// </summary>
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case ValidationFailedException validation:
                await ErrorEnvelopeWriter.WriteAsync(httpContext, StatusCodes.Status400BadRequest, validation.Code, validation.Message, validation.Errors);
                return true;

            case ConflictException conflict:
                await ErrorEnvelopeWriter.WriteAsync(httpContext, StatusCodes.Status409Conflict, conflict.Code, conflict.Message);
                return true;

            case NotFoundException notFound:
                await ErrorEnvelopeWriter.WriteAsync(httpContext, StatusCodes.Status404NotFound, notFound.Code, notFound.Message);
                return true;

            case ForbiddenAccessException forbidden:
                await ErrorEnvelopeWriter.WriteAsync(httpContext, StatusCodes.Status403Forbidden, forbidden.Code, forbidden.Message);
                return true;

            case BadHttpRequestException badRequest:
                // Malformed body / binding failure. Framework messages may echo input; keep them generic.
                await ErrorEnvelopeWriter.WriteAsync(
                    httpContext,
                    badRequest.StatusCode,
                    ErrorEnvelopeWriter.CodeForStatus(badRequest.StatusCode),
                    "The request could not be read.");
                return true;

            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                return true;

            default:
                logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
                await ErrorEnvelopeWriter.WriteAsync(
                    httpContext,
                    StatusCodes.Status500InternalServerError,
                    ErrorCodes.Internal,
                    "An unexpected error occurred. Quote the correlationId when contacting support.");
                return true;
        }
    }
}
