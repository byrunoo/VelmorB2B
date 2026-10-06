using Commercial.Application;
using Commercial.Infrastructure;
using Commercial.Infrastructure.BackgroundJobs;
using Commercial.Infrastructure.Hosting;
using Commercial.Worker.Jobs;

// Worker host: Hangfire server + outbox dispatch. Exposes only health endpoints over HTTP.
var builder = WebApplication.CreateBuilder(args);

builder.AddCommercialObservability("commercial-worker");

builder.Services.AddCommercialApplication();
builder.Services.AddCommercialInfrastructure(builder.Configuration);
builder.Services.AddCommercialHangfireServer(builder.Configuration);
builder.Services.AddTransient<OutboxDispatchJob>();
builder.Services.AddHostedService<RecurringJobRegistration>();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.MapCommercialHealthEndpoints();

await app.RunAsync();
