using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commercial.IntegrationTests.Infrastructure;
using Commercial.Worker.Jobs;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.IntegrationTests.Hosts;

public static class HangfireProbe
{
    public static readonly System.Collections.Concurrent.ConcurrentBag<string> Executions = [];

    public static void Touch(string marker) => Executions.Add(marker);
}

/// <summary>Runtime proof: real API + real Worker hosts, Hangfire on PostgreSQL, end-to-end outbox dispatch.</summary>
public sealed class WorkerHangfireTests(ContainersFixture containers)
{
    [Fact]
    [Trait("A013", "hangfire-dispatch")]
    public async Task Api_outbox_message_is_dispatched_by_the_worker_hangfire_job_end_to_end()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var worker = new WorkerHostFactory(cs, containers.RedisConnectionString);
        _ = worker.Services; // starts the Worker host: Hangfire server + recurring dispatch job
        await using var api = new ApiHostFactory(cs, containers.RedisConnectionString);
        using var client = api.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/diagnostics/outbox-pings")
        {
            Content = JsonContent.Create(new { note = "e2e" }),
        };
        request.Headers.Add("X-Correlation-Id", "e2e-correlation-42");
        using var created = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var accepted = await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var messageId = accepted.GetProperty("messageId").GetGuid();
        Assert.Equal("e2e-correlation-42", accepted.GetProperty("correlationId").GetString());

        JsonElement status = default;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/diagnostics/outbox-pings/{messageId}", TestContext.Current.CancellationToken);
            if (status.GetProperty("outboxStatus").GetString() == "Processed")
            {
                break;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        Assert.Equal("Processed", status.GetProperty("outboxStatus").GetString());
        Assert.True(status.GetProperty("handlerEffectRecorded").GetBoolean());
        Assert.Equal("e2e-correlation-42", status.GetProperty("correlationId").GetString());

        // Hangfire state is persisted in PostgreSQL (schema "hangfire").
        var storage = worker.Services.GetRequiredService<JobStorage>();
        using var connection = storage.GetConnection();
        Assert.Contains(connection.GetRecurringJobs(), j => j.Id == OutboxDispatchJob.RecurringJobId && j.LastExecution is not null);
        Assert.True(storage.GetMonitoringApi().SucceededListCount() > 0);
    }

    [Fact]
    public async Task Hangfire_jobs_are_persisted_in_postgresql_and_executed_by_the_worker()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var worker = new WorkerHostFactory(cs, containers.RedisConnectionString);
        var client = worker.Services.GetRequiredService<IBackgroundJobClient>();
        var marker = Guid.NewGuid().ToString("N");

        var jobId = client.Enqueue(() => HangfireProbe.Touch(marker));

        var storage = worker.Services.GetRequiredService<JobStorage>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string? state = null;
        while (DateTime.UtcNow < deadline)
        {
            using var connection = storage.GetConnection();
            state = connection.GetStateData(jobId)?.Name;
            if (state == SucceededState.StateName)
            {
                break;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Equal(SucceededState.StateName, state);
        Assert.Contains(marker, HangfireProbe.Executions);
        Assert.NotNull(storage.GetMonitoringApi().JobDetails(jobId)); // job row persisted in PostgreSQL
    }
}
