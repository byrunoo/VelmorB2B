using Commercial.Application.Abstractions.Scope;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Commercial.Api.Security;

/// <summary>Documents the active-context selection headers on every operation that requires authorization.</summary>
internal static class ActiveContextOpenApi
{
    public static Task DocumentSelectionHeaders(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (!metadata.OfType<IAuthorizeData>().Any() || metadata.OfType<IAllowAnonymous>().Any())
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(Header(
            ContextHeaders.ActiveCompanyId,
            "Selection hint (not authority): the company to act in. Validated against the caller's memberships; a company the caller may not use is ACTIVE_COMPANY_FORBIDDEN."));
        operation.Parameters.Add(Header(
            ContextHeaders.ActiveCustomerBindingId,
            "Selection hint (not authority): the caller's own customer binding to act under. Required when the caller has several active bindings (ACTIVE_CUSTOMER_BINDING_REQUIRED)."));
        return Task.CompletedTask;
    }

    private static OpenApiParameter Header(string name, string description) => new()
    {
        Name = name,
        In = ParameterLocation.Header,
        Required = false,
        Description = description,
        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
    };
}
