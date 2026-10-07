using Commercial.Api.Errors;
using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Abstractions.Security;
using Microsoft.AspNetCore.Authorization;

namespace Commercial.Api.Security;

/// <summary>The stage-2 resolution of the current request, for authorization policies.</summary>
internal sealed record ActiveContextFeature(ExecutionScopeResolution Resolution);

/// <summary>Requires the resolved scope to be one of the given kinds (i.e. an active business scope).</summary>
internal sealed class ActiveScopeRequirement(string name, params ExecutionScopeKind[] kinds) : IAuthorizationRequirement
{
    public static ActiveScopeRequirement ActiveCompany { get; } =
        new("active-company", ExecutionScopeKind.WorkforceCompany, ExecutionScopeKind.CustomerBinding);

    public static ActiveScopeRequirement CustomerBinding { get; } =
        new("customer-binding", ExecutionScopeKind.CustomerBinding);

    public string Name { get; } = name;

    public IReadOnlyList<ExecutionScopeKind> Kinds { get; } = kinds;
}

/// <summary>Requires a permission of the ACTIVE scope (never a role name, never another binding's permission).</summary>
internal sealed class PermissionRequirement(Permission permission) : IAuthorizationRequirement
{
    public Permission Permission { get; } = permission;
}

internal sealed class ActiveContextFailureReason(IAuthorizationHandler handler, ActiveContextError error)
    : AuthorizationFailureReason(handler, ActiveContextErrors.Code(error))
{
    public ActiveContextError Error { get; } = error;
}

internal sealed class ActiveScopeAuthorizationHandler(IExecutionScopeAccessor scopeAccessor) : AuthorizationHandler<ActiveScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveScopeRequirement requirement)
    {
        var scope = scopeAccessor.Current;
        if (requirement.Kinds.Contains(scope.Kind))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Explain WHY there is no business scope (selection required / unavailable) when it is the caller's own
        // state; a principal of the wrong type just gets the generic FORBIDDEN.
        var pending = (context.Resource as HttpContext)?.Features.Get<ActiveContextFeature>()?.Resolution.Pending ?? ActiveContextError.None;
        var wrongType = requirement == ActiveScopeRequirement.CustomerBinding && scope.PrincipalType != PrincipalType.Customer;
        if (pending != ActiveContextError.None && !wrongType)
        {
            context.Fail(new ActiveContextFailureReason(this, pending));
        }
        else
        {
            context.Fail();
        }

        return Task.CompletedTask;
    }
}

internal sealed class PermissionAuthorizationHandler(IExecutionScopeAccessor scopeAccessor) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var scope = scopeAccessor.Current;
        if (scope.HasBusinessScope && scope.HasPermission(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal static class ActiveContextErrorWriter
{
    public static Task WriteAsync(HttpContext context, ActiveContextError error) =>
        ErrorEnvelopeWriter.WriteAsync(
            context,
            ActiveContextErrors.Category(error) == ActiveContextErrorCategory.BadRequest ? StatusCodes.Status400BadRequest : StatusCodes.Status403Forbidden,
            ActiveContextErrors.Code(error),
            ActiveContextErrors.Message(error));
}
