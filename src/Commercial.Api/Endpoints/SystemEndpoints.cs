using System.Reflection;
using Commercial.Api.Errors;
using Commercial.Api.Security;
using Commercial.Application.Abstractions.Scope;

namespace Commercial.Api.Endpoints;

public sealed record VersionResponse(string Service, string Version, string Environment, string Runtime);

/// <summary>
/// The caller's resolved execution scope. <c>PendingSelection</c> carries the active-context error code when no
/// business scope is active yet (e.g. <c>ACTIVE_CUSTOMER_BINDING_REQUIRED</c>); it never lists other memberships.
/// </summary>
public sealed record CurrentScopeResponse(
    string PrincipalType,
    string ScopeKind,
    Guid? PrincipalId,
    Guid? TenantId,
    Guid? ActiveCompanyId,
    Guid? CustomerAccountId,
    Guid? ActiveCustomerBindingId,
    IReadOnlyList<string> Permissions,
    string ScopeFingerprint,
    string? PendingSelection,
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

        system.MapGet("/me", (HttpContext http, IExecutionScopeAccessor scope) =>
            {
                var s = scope.Current;
                var pending = http.Features.Get<ActiveContextFeature>()?.Resolution.Pending ?? ActiveContextError.None;
                return TypedResults.Ok(new CurrentScopeResponse(
                    PrincipalTypeValues.ToWire(s.PrincipalType),
                    s.Kind.ToString(),
                    s.PrincipalId?.Value,
                    s.TenantId?.Value,
                    s.ActiveCompanyId?.Value,
                    s.CustomerAccountId?.Value,
                    s.ActiveCustomerBindingId?.Value,
                    s.Permissions.CanonicalCodes,
                    s.Fingerprint.Value,
                    pending == ActiveContextError.None ? null : ActiveContextErrors.Code(pending),
                    s.CorrelationId));
            })
            .RequireAuthorization(CommercialPolicies.Authenticated)
            .WithName("GetCurrentScope")
            .WithSummary("The server-side execution scope of the caller (identity-only when no business scope is active).")
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest, ErrorEnvelopeWriter.ContentType)
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized, ErrorEnvelopeWriter.ContentType)
            .Produces<ErrorEnvelope>(StatusCodes.Status403Forbidden, ErrorEnvelopeWriter.ContentType);

        system.MapGet("/access-probe/workforce", () =>
                TypedResults.Ok(new AccessProbeResponse(true, PrincipalTypeValues.Workforce)))
            .RequireAuthorization(CommercialPolicies.Workforce)
            .WithName("ProbeWorkforceAccess")
            .WithSummary("Authorization-policy probe: WORKFORCE identities only (401 vs 403 contract).")
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized, ErrorEnvelopeWriter.ContentType)
            .Produces<ErrorEnvelope>(StatusCodes.Status403Forbidden, ErrorEnvelopeWriter.ContentType);

        system.MapGet("/access-probe/customer", () =>
                TypedResults.Ok(new AccessProbeResponse(true, PrincipalTypeValues.Customer)))
            .RequireAuthorization(CommercialPolicies.Customer)
            .WithName("ProbeCustomerAccess")
            .WithSummary("Authorization-policy probe: CUSTOMER identities with exactly one resolved active customer binding.")
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest, ErrorEnvelopeWriter.ContentType)
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized, ErrorEnvelopeWriter.ContentType)
            .Produces<ErrorEnvelope>(StatusCodes.Status403Forbidden, ErrorEnvelopeWriter.ContentType);

        system.MapGet("/access-probe/active-company", (IExecutionScopeAccessor scope) =>
                TypedResults.Ok(new AccessProbeResponse(true, PrincipalTypeValues.ToWire(scope.Current.PrincipalType))))
            .RequireAuthorization(CommercialPolicies.ActiveCompany)
            .WithName("ProbeActiveCompanyScope")
            .WithSummary("Authorization-policy probe: requires exactly one active company (ACTIVE_COMPANY_REQUIRED contract).")
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest, ErrorEnvelopeWriter.ContentType)
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized, ErrorEnvelopeWriter.ContentType)
            .Produces<ErrorEnvelope>(StatusCodes.Status403Forbidden, ErrorEnvelopeWriter.ContentType);

        system.MapGet("/access-probe/customer-users-manage", () =>
                TypedResults.Ok(new AccessProbeResponse(true, PrincipalTypeValues.Customer)))
            .RequireAuthorization(CommercialPolicies.CustomerUsersManage)
            .WithName("ProbeCustomerUsersManage")
            .WithSummary("Authorization-policy probe: binding-scoped permission customer.users.manage of the ACTIVE binding.")
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest, ErrorEnvelopeWriter.ContentType)
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized, ErrorEnvelopeWriter.ContentType)
            .Produces<ErrorEnvelope>(StatusCodes.Status403Forbidden, ErrorEnvelopeWriter.ContentType);

        return v1;
    }
}
