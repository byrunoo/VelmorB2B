using Hangfire;

namespace Commercial.Worker.Jobs;

/// <summary>Registers the Phase 0 recurring jobs once the host has started.</summary>
internal sealed class RecurringJobRegistration(IRecurringJobManager recurringJobs, IConfiguration configuration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        recurringJobs.AddOrUpdate<OutboxDispatchJob>(
            OutboxDispatchJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None),
            configuration["Worker:OutboxDispatchCron"] ?? OutboxDispatchJob.Schedule,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
