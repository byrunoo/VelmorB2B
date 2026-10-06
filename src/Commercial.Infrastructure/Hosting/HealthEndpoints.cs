using System.Text.Json;
using Commercial.Infrastructure.BackgroundJobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Commercial.Infrastructure.Hosting;

/// <summary>
/// Liveness (<c>/health/live</c>): process is up — no dependency checks, so a DB outage does not
/// trigger restarts. Readiness (<c>/health/ready</c>): PostgreSQL, Redis and Hangfire storage.
/// Responses never include exception details.
/// </summary>
public static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    public static IEndpointRouteBuilder MapCommercialHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthTags.Live),
            ResponseWriter = WriteAsync,
        }).AllowAnonymous().ExcludeFromDescription();

        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthTags.Ready),
            ResponseWriter = WriteAsync,
        }).AllowAnonymous().ExcludeFromDescription();

        return endpoints;
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            correlationId = CorrelationId.Get(context),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                description = e.Value.Description,
            }),
        };
        return JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonSerializerOptions.Web, context.RequestAborted);
    }
}
