using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace Commercial.Infrastructure.Hosting;

public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";
    public const int MaxLength = 128;
    private const string ItemKey = "commercial.correlation_id";

    /// <summary>Accepts caller-provided IDs only when they are short and use a safe character set.</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static string Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) && value is string id ? id : context.TraceIdentifier;
    }

    internal static void Set(HttpContext context, string id) => context.Items[ItemKey] = id;
}

/// <summary>
/// Reads/creates the correlation ID, echoes it on the response, tags the current trace and pushes it
/// into the Serilog log context. Registered first in every host pipeline.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var incoming = context.Request.Headers[CorrelationId.HeaderName].ToString();
        var id = CorrelationId.IsValid(incoming) ? incoming : CorrelationId.NewId();

        CorrelationId.Set(context, id);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationId.HeaderName] = id;
            return Task.CompletedTask;
        });

        Activity.Current?.SetTag("commercial.correlation_id", id);
        using (LogContext.PushProperty("CorrelationId", id))
        {
            await next(context);
        }
    }
}
