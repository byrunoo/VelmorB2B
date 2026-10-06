using Commercial.Application.Abstractions.Messaging;
using Commercial.Application.Abstractions.Requests;
using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Commercial.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddCommercialApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IExecutionScopeAccessor, AmbientExecutionScopeAccessor>();
        services.TryAddScoped<IRequestDispatcher, RequestDispatcher>();

        // Diagnostics (Phase 0 infrastructure proof, not business).
        services.AddScoped<ICommandHandler<EnqueueDiagnosticPing, DiagnosticPingAccepted>, EnqueueDiagnosticPingHandler>();
        services.AddScoped<IRequestValidator<EnqueueDiagnosticPing>, EnqueueDiagnosticPingValidator>();
        services.AddScoped<IQueryHandler<GetDiagnosticPingStatus, DiagnosticPingStatus?>, GetDiagnosticPingStatusHandler>();
        services.AddScoped<IOutboxMessageHandler, DiagnosticPingOutboxHandler>();

        return services;
    }
}
