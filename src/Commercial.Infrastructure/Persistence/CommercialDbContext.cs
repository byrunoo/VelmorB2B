using Commercial.Infrastructure.Messaging.Outbox;
using Commercial.Infrastructure.Persistence.Conventions;
using Commercial.Infrastructure.Persistence.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Commercial.Infrastructure.Persistence;

/// <summary>
/// The single Commercial Platform DbContext. Phase 0 contains ONLY runtime-infrastructure tables
/// (outbox, diagnostic receipts). Business tables arrive with their phases.
/// Naming: snake_case, schema <c>commercial</c>. Time: UTC only (timestamptz / DateTimeOffset).
/// Tenant query filters are intentionally absent until ADR-0001 is decided.
/// </summary>
public sealed class CommercialDbContext(DbContextOptions<CommercialDbContext> options) : DbContext(options)
{
    public const string Schema = "commercial";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<DiagnosticPingReceipt> DiagnosticPingReceipts => Set<DiagnosticPingReceipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CommercialDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // UTC policy: DateTime values must be UTC (DateTimeOffset is preferred and is mapped to timestamptz).
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }
}
