using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commercial.Infrastructure.Persistence;
using Commercial.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Commercial.IntegrationTests.Hosts;

/// <summary>Runtime validation of the API host against real PostgreSQL/Redis/Hangfire storage.</summary>
public sealed class ApiRuntimeTests(ContainersFixture containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Liveness_and_readiness_report_all_dependencies()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var api = new ApiHostFactory(cs, containers.RedisConnectionString);
        using var client = api.CreateClient();

        using var live = await client.GetAsync("/health/live", Ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        using var ready = await client.GetAsync("/health/ready", Ct);
        var body = await ready.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        var checks = body.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("Healthy", checks["postgresql"]);
        Assert.Equal("Healthy", checks["redis"]);
        Assert.Equal("Healthy", checks["hangfire-storage"]);
    }

    [Fact]
    public async Task Readiness_fails_but_liveness_stays_up_when_a_dependency_is_down()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var api = new ApiHostFactory(cs, "127.0.0.1:1,connectTimeout=300,syncTimeout=500,asyncTimeout=500");
        using var client = api.CreateClient();

        using var live = await client.GetAsync("/health/live", Ct);
        using var ready = await client.GetAsync("/health/ready", Ct);
        var body = await ready.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains(body.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "redis" && c.GetProperty("status").GetString() == "Unhealthy");
        Assert.DoesNotContain("Exception", body.GetRawText(), StringComparison.Ordinal); // no internals leaked
    }

    [Fact]
    public async Task Correlation_id_is_echoed_generated_sanitized_and_persisted_with_outbox_rows()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var api = new ApiHostFactory(cs, containers.RedisConnectionString);
        using var client = api.CreateClient();

        using var echoed = await SendAsync(client, HttpMethod.Get, "/api/v1/system/version", "client-corr-1");
        Assert.Equal("client-corr-1", echoed.Headers.GetValues("X-Correlation-Id").Single());

        using var generated = await client.GetAsync("/api/v1/system/version", Ct);
        Assert.Matches("^[0-9a-f]{32}$", generated.Headers.GetValues("X-Correlation-Id").Single());

        using var sanitized = await SendAsync(client, HttpMethod.Get, "/api/v1/system/version", "bad value <script>");
        Assert.Matches("^[0-9a-f]{32}$", sanitized.Headers.GetValues("X-Correlation-Id").Single());

        using var error = await SendAsync(client, HttpMethod.Get, "/api/v1/system/me", "client-corr-401");
        var envelope = await error.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("client-corr-401", envelope.GetProperty("correlationId").GetString());

        using var ping = await SendAsync(client, HttpMethod.Post, "/api/v1/diagnostics/outbox-pings", "client-corr-outbox", new { note = "corr" });
        var messageId = (await ping.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("messageId").GetGuid();
        await using var scope = api.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<CommercialDbContext>().OutboxMessages.SingleAsync(m => m.Id == messageId, Ct);
        Assert.Equal("client-corr-outbox", row.CorrelationId);
    }

    [Fact]
    public async Task OpenTelemetry_traces_requests_and_error_envelope_carries_the_trace_id()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        var exported = new List<Activity>();
        await using var api = new ApiHostFactory(cs, containers.RedisConnectionString,
            services => services.ConfigureOpenTelemetryTracerProvider(b => b.AddInMemoryExporter(exported)));
        using var client = api.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, "/api/v1/system/me", "otel-corr-1");
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var traceId = envelope.GetProperty("traceId").GetString();
        Assert.Matches("^[0-9a-f]{32}$", traceId!);

        api.Services.GetRequiredService<TracerProvider>().ForceFlush();
        Activity? server = null;
        for (var i = 0; i < 40 && server is null; i++)
        {
            lock (exported)
            {
                server = exported.FirstOrDefault(a => a.Kind == ActivityKind.Server && a.TraceId.ToString() == traceId);
            }

            if (server is null)
            {
                await Task.Delay(50, Ct);
            }
        }

        Assert.NotNull(server);
        Assert.Equal("otel-corr-1", server!.GetTagItem("commercial.correlation_id"));
        Assert.Equal(401, server.GetTagItem("http.response.status_code"));
    }

    [Fact]
    public async Task Redis_round_trip_works()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var api = new ApiHostFactory(cs, containers.RedisConnectionString);
        var redis = api.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase();
        var key = "it:" + Guid.NewGuid().ToString("N");

        Assert.True(await redis.StringSetAsync(key, "value", TimeSpan.FromMinutes(1)));
        Assert.Equal("value", (string?)await redis.StringGetAsync(key));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string correlationId, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, Ct);
    }
}
