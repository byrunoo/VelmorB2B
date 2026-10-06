using Commercial.Api.Errors;
using Commercial.Application.Abstractions.Requests;
using Commercial.Application.Diagnostics;
using Commercial.Application.Errors;

namespace Commercial.Api.Endpoints;

public sealed record CreateOutboxPingRequest(string? Note);

/// <summary>
/// Developer diagnostics (mapped only when <c>Diagnostics:EnableEndpoints=true</c>).
/// Endpoints only translate HTTP to Application requests — no persistence access here (A006).
/// </summary>
internal static class DiagnosticsEndpoints
{
    public static RouteGroupBuilder MapDiagnosticsEndpoints(this RouteGroupBuilder v1)
    {
        var diagnostics = v1.MapGroup("/diagnostics").WithTags("Diagnostics").AllowAnonymous();

        diagnostics.MapPost("/outbox-pings", async (CreateOutboxPingRequest? request, IRequestDispatcher dispatcher, CancellationToken ct) =>
            {
                var accepted = await dispatcher.SendAsync(new EnqueueDiagnosticPing(request?.Note), ct);
                return TypedResults.Accepted($"/api/v1/diagnostics/outbox-pings/{accepted.MessageId}", accepted);
            })
            .WithName("CreateOutboxPing")
            .WithSummary("Writes a diagnostic outbox message in an application transaction (dispatched by the Worker).")
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest, ErrorEnvelopeWriter.ContentType);

        diagnostics.MapGet("/outbox-pings/{messageId:guid}", async (Guid messageId, IRequestDispatcher dispatcher, CancellationToken ct) =>
            {
                var status = await dispatcher.QueryAsync(new GetDiagnosticPingStatus(messageId), ct)
                    ?? throw new NotFoundException($"Outbox ping '{messageId}' was not found.");
                return TypedResults.Ok(status);
            })
            .WithName("GetOutboxPing")
            .WithSummary("Dispatch status of a diagnostic outbox message.")
            .Produces<ErrorEnvelope>(StatusCodes.Status404NotFound, ErrorEnvelopeWriter.ContentType);

        return v1;
    }
}
