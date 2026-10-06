using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace Commercial.Infrastructure.Hosting;

public static class ObservabilityRegistration
{
    /// <summary>
    /// Structured logging (Serilog) + OpenTelemetry tracing/metrics for a host.
    /// OTLP export is enabled only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (or
    /// <c>OpenTelemetry:OtlpEndpoint</c>) is configured; otherwise telemetry is collected in-process only.
    /// </summary>
    public static WebApplicationBuilder AddCommercialObservability(this WebApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var serviceVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var isDevelopment = builder.Environment.IsDevelopment();

        builder.Services.AddSerilog((services, logger) =>
        {
            logger
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service", serviceName)
                .Enrich.WithProperty("environment", builder.Environment.EnvironmentName);

            if (isDevelopment)
            {
                logger.WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture, outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} corr={CorrelationId} trace={TraceId} {Message:lj}{NewLine}{Exception}");
            }
            else
            {
                logger.WriteTo.Console(new RenderedCompactJsonFormatter());
            }
        });

        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? builder.Configuration["OpenTelemetry:OtlpEndpoint"];

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: serviceVersion)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .AddSource(CommercialTelemetry.SourceName)
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddMeter(CommercialTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            otel.UseOtlpExporter(OpenTelemetry.Exporter.OtlpExportProtocol.Grpc, new Uri(otlpEndpoint));
        }

        return builder;
    }
}
