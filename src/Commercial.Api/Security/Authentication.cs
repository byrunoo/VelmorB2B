using System.Security.Claims;
using System.Text.Encodings.Web;
using Commercial.Api.Errors;
using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;

namespace Commercial.Api.Security;

public static class CommercialAuthenticationSchemes
{
    /// <summary>Default in every environment until Phase 1 identity: never authenticates.</summary>
    public const string Unauthenticated = "Commercial.Unauthenticated";

    /// <summary>DEVELOPMENT ONLY. Header-based principal for local shells and tests.</summary>
    public const string DevelopmentHeader = "Commercial.DevelopmentHeader";
}

public static class CommercialPolicies
{
    public const string Authenticated = "commercial:authenticated";
    public const string Workforce = "commercial:workforce";
    public const string Customer = "commercial:customer";
}

public static class SecurityRegistration
{
    public static IServiceCollection AddCommercialSecurity(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Extension point: Phase 1 adds the real scheme(s) here (OIDC/JWT for WORKFORCE and CUSTOMER).
        var devEnabled = environment.IsDevelopment() && configuration.GetValue("Authentication:Development:Enabled", false);

        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = devEnabled ? CommercialAuthenticationSchemes.DevelopmentHeader : CommercialAuthenticationSchemes.Unauthenticated;
        });
        auth.AddScheme<AuthenticationSchemeOptions, UnauthenticatedHandler>(CommercialAuthenticationSchemes.Unauthenticated, null);
        if (devEnabled)
        {
            auth.AddScheme<AuthenticationSchemeOptions, DevelopmentHeaderAuthenticationHandler>(CommercialAuthenticationSchemes.DevelopmentHeader, null);
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(CommercialPolicies.Authenticated, p => p.RequireAuthenticatedUser())
            .AddPolicy(CommercialPolicies.Workforce, p => p
                .RequireAuthenticatedUser()
                .RequireClaim(CommercialClaimTypes.PrincipalType, PrincipalTypeValues.Workforce))
            .AddPolicy(CommercialPolicies.Customer, p => p
                .RequireAuthenticatedUser()
                .RequireClaim(CommercialClaimTypes.PrincipalType, PrincipalTypeValues.Customer)
                .RequireClaim(CommercialClaimTypes.CustomerAccountId));

        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ErrorEnvelopeAuthorizationResultHandler>();
        return services;
    }
}

internal sealed class UnauthenticatedHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
}

/// <summary>
/// DEVELOPMENT ONLY — registered only when the host environment is Development AND
/// <c>Authentication:Development:Enabled=true</c>. Reads <c>X-Dev-Principal-Type</c>
/// (WORKFORCE|CUSTOMER) and optional <c>X-Dev-Subject</c>, <c>X-Dev-Tenant-Id</c>,
/// <c>X-Dev-Company-Id</c>, <c>X-Dev-Customer-Account-Id</c>, <c>X-Dev-Sales-Channel-Id</c>.
/// </summary>
internal sealed class DevelopmentHeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var type = Request.Headers["X-Dev-Principal-Type"].ToString().ToUpperInvariant();
        if (type is not (PrincipalTypeValues.Workforce or PrincipalTypeValues.Customer))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(CommercialClaimTypes.PrincipalType, type),
            new(CommercialClaimTypes.Subject, HeaderOr("X-Dev-Subject", "dev-" + type.ToLowerInvariant())),
        };
        AddGuidClaim(claims, "X-Dev-Tenant-Id", CommercialClaimTypes.TenantId);
        AddGuidClaim(claims, "X-Dev-Company-Id", CommercialClaimTypes.CompanyId);
        AddGuidClaim(claims, "X-Dev-Customer-Account-Id", CommercialClaimTypes.CustomerAccountId);
        AddGuidClaim(claims, "X-Dev-Sales-Channel-Id", CommercialClaimTypes.SalesChannelId);

        var identity = new ClaimsIdentity(claims, Scheme.Name, CommercialClaimTypes.Subject, null);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    private string HeaderOr(string header, string fallback)
    {
        var value = Request.Headers[header].ToString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private void AddGuidClaim(List<Claim> claims, string header, string claimType)
    {
        if (Guid.TryParse(Request.Headers[header].ToString(), out var id))
        {
            claims.Add(new Claim(claimType, id.ToString("D")));
        }
    }
}

/// <summary>401 vs 403 are written as the standard error envelope instead of empty bodies.</summary>
internal sealed class ErrorEnvelopeAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await ErrorEnvelopeWriter.WriteAsync(context, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized, "Authentication is required.");
            return;
        }

        if (authorizeResult.Forbidden)
        {
            await ErrorEnvelopeWriter.WriteAsync(context, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "The authenticated principal is not allowed to perform this operation.");
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
