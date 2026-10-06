using System.Reflection;
using Commercial.Api.Errors;
using Commercial.Api.Security;
using Commercial.Application.Abstractions.Scope;

namespace Commercial.Api.Endpoints;

public sealed record VersionResponse(string Service, string Version, string Environment, string Runtime);

public sealed record CurrentScopeResponse(
    string PrincipalType,
    string? PrincipalId,
    Guid? TenantId,
    Guid? CompanyId,
    Guid? CustomerAccountId,
    Guid? SalesChannelId,
    string CorrelationId);

public sealed record AccessProbeResponse(bool Granted, string PrincipalType);

internal static class SystemEndpoints
{
    public static RouteGroupBuilder MapSystemEndpoints(this RouteGroupBuilder v1)
    {
        var system = v1.MapGroup("/system").WithTags("System");

        system.MapGet("/version", (IHostEnvironment env) =>
            {
                var assembly = typeof(SystemEndpoints).Assembly;
                var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
                return TypedResults.Ok(new VersionResponse("Commercial.Api", version.Split('+')[0], env.EnvironmentName, Environment.Version.ToString()));
            })
            .AllowAnonymous()
            .WithName("GetVersion")
            .WithSummary("Service name, version and environment.");

        system.MapGet("/me", (IExecutionScopeAccessor scope) =>
            {
                var s = scope.Current;
                return TypedResults.Ok(new CurrentScopeResponse(
                    PrincipalTypeValues.ToWire(s.PrincipalType) ?? s.PrincipalType.ToString().ToUpperInvariant(),
                    s.PrincipalId,
                    s.TenantId?.Value,
                    s.CompanyId?.Value,
                    s.CustomerAccountId?.Value,
                    s.SalesChannelId?.Value,
                    s.CorrelationId));
            })
            .RequireAuthorization(CommercialPolicies.Authenticated)
            .WithName("GetCurrentScope")
            .WithSummary("The server-side execution scope of the caller.")
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized, ErrorEnvelopeWriter.ContentType);

        system.MapGet("/access-probe/workforce", (IExecutionScopeAccessor scope) =>
                TypedResults.Ok(new AccessProbeResponse(true, PrincipalTypeValues.Workforce)))
            .RequireAuthorization(CommercialPolicies.Workforce)
            .WithName("ProbeWorkforceAccess")
            .WithSummary("Authorization-policy probe: WORKFORCE principals only (401 vs 403 contract).")
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized, ErrorEnvelopeWriter.ContentType)
            .Produces<ErrorEnvelope>(StatusCodes.Status403Forbidden, ErrorEnvelopeWriter.ContentType);

        system.MapGet("/access-probe/customer", (IExecutionScopeAccessor scope) =>
                TypedResults.Ok(new AccessProbeResponse(true, PrincipalTypeValues.Customer)))
            .RequireAuthorization(CommercialPolicies.Customer)
            .WithName("ProbeCustomerAccess")
            .WithSummary("Authorization-policy probe: CUSTOMER principals bound to a customer account only.")
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized, ErrorEnvelopeWriter.ContentType)
            .Produces<ErrorEnvelope>(StatusCodes.Status403Forbidden, ErrorEnvelopeWriter.ContentType);

        return v1;
    }
}
