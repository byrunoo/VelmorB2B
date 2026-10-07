using System.Security.Claims;
using System.Text.Encodings.Web;
using Commercial.Api.Errors;
using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Abstractions.Security;
using Commercial.Application.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;

namespace Commercial.Api.Security;

public static class CommercialAuthenticationSchemes
{
    /// <summary>Default in every environment until the OIDC workstream: never authenticates.</summary>
    public const string Unauthenticated = "Commercial.Unauthenticated";

    /// <summary>DEVELOPMENT ONLY. Header-based identity (stage 1 only) for local shells and tests.</summary>
    public const string DevelopmentHeader = "Commercial.DevelopmentHeader";

    /// <summary>Issuer stamped on development identities so they can never collide with a real IdP issuer.</summary>
    public const string DevelopmentIssuer = "urn:commercial:development";
}

public static class CommercialPolicies
{
    /// <summary>Any authenticated identity (business scope not required).</summary>
    public const string Authenticated = "commercial:authenticated";

    /// <summary>WORKFORCE identity (business scope not required).</summary>
    public const string Workforce = "commercial:workforce";

    /// <summary>CUSTOMER identity with ONE resolved active customer binding.</summary>
    public const string Customer = "commercial:customer";

    /// <summary>Exactly one active company (WORKFORCE company scope or CUSTOMER binding scope).</summary>
    public const string ActiveCompany = "commercial:active-company";

    /// <summary>Probe for binding-scoped permissions: CUSTOMER binding scope + <c>customer.users.manage</c>.</summary>
    public const string CustomerUsersManage = "commercial:customer-users-manage";
}

public static class SecurityRegistration
{
    public static IServiceCollection AddCommercialSecurity(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Extension point: the OIDC workstream adds the real scheme(s) here (WORKFORCE and CUSTOMER issuers).
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
                .AddRequirements(ActiveScopeRequirement.CustomerBinding))
            .AddPolicy(CommercialPolicies.ActiveCompany, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(ActiveScopeRequirement.ActiveCompany))
            .AddPolicy(CommercialPolicies.CustomerUsersManage, p => p
                .RequireAuthenticatedUser()
                .RequireClaim(CommercialClaimTypes.PrincipalType, PrincipalTypeValues.Customer)
                .AddRequirements(ActiveScopeRequirement.CustomerBinding, new PermissionRequirement(SecurityPermissions.CustomerUsersManage)));

        services.AddSingleton<IAuthorizationHandler, ActiveScopeAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
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
/// <c>Authentication:Development:Enabled=true</c>. Produces a stage-1 identity ONLY: <c>X-Dev-Principal-Type</c>
/// (WORKFORCE|CUSTOMER) and optional <c>X-Dev-Subject</c>, issuer <see cref="CommercialAuthenticationSchemes.DevelopmentIssuer"/>.
/// It deliberately carries no tenant / company / customer values — business scope is resolved server-side.
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

        var subject = Request.Headers["X-Dev-Subject"].ToString();
        var claims = new List<Claim>
        {
            new(CommercialClaimTypes.Issuer, CommercialAuthenticationSchemes.DevelopmentIssuer),
            new(CommercialClaimTypes.PrincipalType, type),
            new(CommercialClaimTypes.Subject, string.IsNullOrWhiteSpace(subject) ? "dev-" + type.ToLowerInvariant() : subject),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name, CommercialClaimTypes.Subject, null);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

/// <summary>401 vs 403 (and active-context 400s) are written as the standard error envelope instead of empty bodies.</summary>
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
            var contextError = authorizeResult.AuthorizationFailure?.FailureReasons
                .OfType<ActiveContextFailureReason>()
                .Select(r => r.Error)
                .FirstOrDefault(e => e != ActiveContextError.None) ?? ActiveContextError.None;

            if (contextError != ActiveContextError.None)
            {
                await ActiveContextErrorWriter.WriteAsync(context, contextError);
                return;
            }

            await ErrorEnvelopeWriter.WriteAsync(context, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "The authenticated principal is not allowed to perform this operation.");
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
