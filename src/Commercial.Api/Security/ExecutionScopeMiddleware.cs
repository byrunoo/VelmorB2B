using System.Security.Claims;
using Commercial.Application.Abstractions.Scope;
using Commercial.Domain.Identity;
using Commercial.Infrastructure.Hosting;

namespace Commercial.Api.Security;

/// <summary>
/// Builds the server-side <see cref="ExecutionScope"/> from the authenticated principal and the
/// correlation ID. Scope values come ONLY from validated claims — never from request bodies/query.
/// </summary>
internal sealed class ExecutionScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IExecutionScopeAccessor accessor)
    {
        using (accessor.Begin(FromPrincipal(context.User, CorrelationId.Get(context))))
        {
            await next(context);
        }
    }

    internal static ExecutionScope FromPrincipal(ClaimsPrincipal user, string correlationId)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return ExecutionScope.Anonymous(correlationId);
        }

        return new ExecutionScope
        {
            PrincipalType = PrincipalTypeValues.Parse(user.FindFirstValue(CommercialClaimTypes.PrincipalType)),
            PrincipalId = user.FindFirstValue(CommercialClaimTypes.Subject),
            TenantId = ParseGuid(user, CommercialClaimTypes.TenantId) is { } t ? new TenantId(t) : null,
            CompanyId = ParseGuid(user, CommercialClaimTypes.CompanyId) is { } c ? new CompanyId(c) : null,
            CustomerAccountId = ParseGuid(user, CommercialClaimTypes.CustomerAccountId) is { } a ? new CustomerAccountId(a) : null,
            SalesChannelId = ParseGuid(user, CommercialClaimTypes.SalesChannelId) is { } s ? new SalesChannelId(s) : null,
            CorrelationId = correlationId,
        };
    }

    private static Guid? ParseGuid(ClaimsPrincipal user, string claimType) =>
        Guid.TryParse(user.FindFirstValue(claimType), out var id) ? id : null;
}
