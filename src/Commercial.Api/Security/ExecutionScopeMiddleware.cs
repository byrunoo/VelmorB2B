using System.Security.Claims;
using Commercial.Application.Abstractions.Scope;
using Commercial.Infrastructure.Hosting;

namespace Commercial.Api.Security;

/// <summary>
/// HTTP seam of the two-stage security context (runs after authentication, before authorization):
/// <list type="number">
/// <item>stage 1 — <see cref="AuthenticatedIdentityContext"/> from validated claims only (issuer, subject, principal type);</item>
/// <item>parse the selection headers (<see cref="ContextHeaders"/>) — syntax only, 400 on malformed;</item>
/// <item>stage 2 — <see cref="IExecutionScopeResolver"/> resolves ONE active business scope from server-side memberships;</item>
/// <item>establish the immutable scope for the request flow; rejected selections never reach the endpoint.</item>
/// </list>
/// Scope values never come from request bodies, query strings or trusted tenant/account headers.
/// </summary>
internal sealed class ExecutionScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IExecutionScopeAccessor accessor, IExecutionScopeResolver resolver)
    {
        var correlationId = CorrelationId.Get(context);
        if (context.User.Identity?.IsAuthenticated != true)
        {
            using (accessor.Begin(ExecutionScope.Anonymous(correlationId)))
            {
                await next(context);
            }

            return;
        }

        var resolution = await ResolveAsync(context, resolver, correlationId);
        context.Features.Set(new ActiveContextFeature(resolution));

        using (accessor.Begin(resolution.Scope))
        {
            if (resolution.IsRejected)
            {
                await ActiveContextErrorWriter.WriteAsync(context, resolution.Error);
                return;
            }

            await next(context);
        }
    }

    private static async Task<ExecutionScopeResolution> ResolveAsync(HttpContext context, IExecutionScopeResolver resolver, string correlationId)
    {
        var identity = TryCreateIdentity(context.User, correlationId);
        if (identity is null)
        {
            // Authenticated but without a valid stage-1 identity (missing issuer/subject/type): fail closed.
            return ExecutionScopeResolution.Rejected(ExecutionScope.Anonymous(correlationId), ActiveContextError.ExecutionScopeInvalid);
        }

        var (selection, parseError) = ActiveContextRequest.Parse(
            context.Request.Headers[ContextHeaders.ActiveCompanyId],
            context.Request.Headers[ContextHeaders.ActiveCustomerBindingId]);
        if (parseError != ActiveContextError.None || selection is null)
        {
            return ExecutionScopeResolution.Rejected(ExecutionScope.ForIdentity(identity, null), parseError);
        }

        return await resolver.ResolveAsync(identity, selection, context.RequestAborted);
    }

    internal static AuthenticatedIdentityContext? TryCreateIdentity(ClaimsPrincipal user, string correlationId)
    {
        var issuer = user.FindFirstValue(CommercialClaimTypes.Issuer);
        var subject = user.FindFirstValue(CommercialClaimTypes.Subject);
        var type = PrincipalTypeValues.Parse(user.FindFirstValue(CommercialClaimTypes.PrincipalType));
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)
            || type is not (PrincipalType.Workforce or PrincipalType.Customer))
        {
            return null;
        }

        return new AuthenticatedIdentityContext(issuer, subject, type, correlationId);
    }
}
