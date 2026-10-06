using System.Diagnostics;
using System.Text.Json;
using Commercial.Application.Errors;
using Commercial.Infrastructure.Hosting;

namespace Commercial.Api.Errors;

/// <summary>
/// The stable Commercial API error contract (RFC 9457 compatible + <c>code</c>, <c>correlationId</c>,
/// <c>traceId</c>, <c>errors</c>). Schema snapshot: tests/Commercial.ContractTests/Schemas/error-envelope.schema.json.
/// </summary>
public sealed record ErrorEnvelope(
    string Type,
    string Title,
    int Status,
    string Code,
    string? Detail,
    string? Instance,
    string CorrelationId,
    string? TraceId,
    IReadOnlyDictionary<string, string[]> Errors);

public static class ErrorEnvelopeWriter
{
    public const string ContentType = "application/problem+json";

    private static readonly IReadOnlyDictionary<string, string[]> NoErrors = new Dictionary<string, string[]>();

    public static ErrorEnvelope Create(
        HttpContext context,
        int status,
        string code,
        string? detail = null,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ErrorEnvelope(
            Type: $"urn:commercial:error:{code.ToLowerInvariant().Replace('_', '-')}",
            Title: TitleFor(status),
            Status: status,
            Code: code,
            Detail: detail,
            Instance: context.Request.Path.HasValue ? context.Request.Path.Value : null,
            CorrelationId: CorrelationId.Get(context),
            TraceId: Activity.Current?.TraceId.ToString(),
            Errors: errors ?? NoErrors);
    }

    public static async Task WriteAsync(
        HttpContext context,
        int status,
        string code,
        string? detail = null,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var envelope = Create(context, status, code, detail, errors);
        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        await JsonSerializer.SerializeAsync(context.Response.Body, envelope, JsonSerializerOptions.Web, context.RequestAborted);
    }

    public static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ErrorCodes.BadRequest,
        StatusCodes.Status401Unauthorized => ErrorCodes.Unauthorized,
        StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => ErrorCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => "METHOD_NOT_ALLOWED",
        StatusCodes.Status409Conflict => ErrorCodes.Conflict,
        StatusCodes.Status415UnsupportedMediaType => "UNSUPPORTED_MEDIA_TYPE",
        StatusCodes.Status503ServiceUnavailable => ErrorCodes.ServiceUnavailable,
        >= 500 => ErrorCodes.Internal,
        _ => "HTTP_" + status.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string TitleFor(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        415 => "Unsupported Media Type",
        422 => "Unprocessable Entity",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Error",
    };
}
