using System.Text.Json.Serialization;
using Commercial.Api.Endpoints;
using Commercial.Api.Errors;
using Commercial.Api.Security;
using Commercial.Application;
using Commercial.Infrastructure;
using Commercial.Infrastructure.Hosting;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddCommercialObservability("commercial-api");

builder.Services.AddCommercialApplication();
builder.Services.AddCommercialInfrastructure(builder.Configuration);
builder.Services.AddCommercialSecurity(builder.Configuration, builder.Environment);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi("v1", o =>
{
    o.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Commercial Platform API";
        document.Info.Version = "v1";
        document.Info.Description = "Commercial Platform HTTP contract: system, diagnostics, the standard error envelope and active-context selection.";
        document.Servers?.Clear();
        return Task.CompletedTask;
    });
    o.AddOperationTransformer(ActiveContextOpenApi.DocumentSelectionHeaders);
});

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
    : ctx.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ? Serilog.Events.LogEventLevel.Verbose
    : Serilog.Events.LogEventLevel.Information);
app.UseExceptionHandler();
app.UseStatusCodePages(async ctx =>
{
    // Framework-generated empty error responses (404 route miss, 405, 415...) get the standard envelope.
    var http = ctx.HttpContext;
    if (!http.Response.HasStarted && http.Response.ContentLength is null && string.IsNullOrEmpty(http.Response.ContentType))
    {
        await ErrorEnvelopeWriter.WriteAsync(http, http.Response.StatusCode, ErrorEnvelopeWriter.CodeForStatus(http.Response.StatusCode));
    }
});

app.UseAuthentication();
app.UseMiddleware<ExecutionScopeMiddleware>();
app.UseAuthorization();

app.MapCommercialHealthEndpoints();

if (app.Configuration.GetValue("OpenApi:Enabled", app.Environment.IsDevelopment()))
{
    app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
}

var v1 = app.MapGroup("/api/v1");
v1.MapSystemEndpoints();
if (app.Configuration.GetValue("Diagnostics:EnableEndpoints", false))
{
    v1.MapDiagnosticsEndpoints();
}

await app.RunAsync();
