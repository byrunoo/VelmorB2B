using System.Data.Common;
using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Abstractions.Security;
using Commercial.Domain.Identity;
using Commercial.Infrastructure.Persistence;
using Commercial.Infrastructure.Persistence.Session;
using Commercial.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Commercial.IntegrationTests.Spikes.TenantIsolation.RlsSpikeDatabase;

namespace Commercial.IntegrationTests.Spikes.TenantIsolation;

/// <summary>ADR-0001 SPIKE — EF Core global query filters vs RLS, and the Phase 0 session-initializer seam.</summary>
[Trait("ADR-0001", "spike")]
public sealed class EfFilterAndSeamSpikeTests(ContainersFixture containers)
{
    [Fact] // scenario 11
    public async Task IgnoreQueryFilters_bypasses_EF_isolation_but_not_RLS()
    {
        var db = await CreateAsync(containers);

        // Option A alone (no RLS in effect: connection that is not subject to policies).
        await using (var ef = SpikeContext.Create(db.MaintenanceConnectionString, TenantA))
        {
            Assert.Equal(5, await ef.Rows.CountAsync());                       // filter applied
            Assert.Equal(6, await ef.Rows.IgnoreQueryFilters().CountAsync());  // one call away from cross-tenant
            Assert.Equal(6, await ef.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM spike.scoped_rows").SingleAsync()); // raw SQL ignores EF filters
            Assert.Contains("tenant_id", ef.Rows.ToQueryString(), StringComparison.Ordinal);
        }

        // Option C (filter + RLS): the same bypasses are contained by the database.
        await using (var ef = SpikeContext.Create(db.AppConnectionString, TenantA))
        {
            await ef.Database.OpenConnectionAsync();
            await SpikeSession.ApplyAsync(ef.Database.GetDbConnection(), SpikeScope.Customer(TenantA, CompanyA1, CustomerX), transactionLocal: false);
            Assert.Equal(2, await ef.Rows.CountAsync());
            Assert.Equal(2, await ef.Rows.IgnoreQueryFilters().CountAsync());
            Assert.Equal(2, await ef.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM spike.scoped_rows").SingleAsync());
        }
    }

    [Fact] // semantic drift illustration
    public async Task Filter_and_policy_can_disagree_and_the_stricter_one_wins()
    {
        var db = await CreateAsync(containers);
        await using var ef = SpikeContext.Create(db.AppConnectionString, TenantA); // EF filter: tenant only
        await ef.Database.OpenConnectionAsync();
        await SpikeSession.ApplyAsync(ef.Database.GetDbConnection(), SpikeScope.Workforce(TenantA, CompanyA2), transactionLocal: false);

        // EF thinks "tenant A" (5 rows); RLS enforces company A2 (1 row). Debugging must consider both layers.
        Assert.Equal(1, await ef.Rows.CountAsync());
    }

    [Fact] // Phase 0 seam: ExecutionScope -> IDbSessionInitializer -> CommercialDbContext connection -> RLS
    public async Task Phase0_session_initializer_seam_can_drive_rls_through_the_real_DbContext()
    {
        var db = await CreateAsync(containers);
        var pooled = new Npgsql.NpgsqlConnectionStringBuilder(db.AppConnectionString) { MaxPoolSize = 1 }.ConnectionString;
        await using var provider = containers.BuildServices(pooled, before: s => s.AddSingleton<IDbSessionInitializer, RlsSpikeSessionInitializer>());
        var accessor = provider.GetRequiredService<IExecutionScopeAccessor>();

        async Task<List<string>> ReadAs(ExecutionScope scope)
        {
            using var _ = accessor.Begin(scope);
            await using var s = provider.CreateAsyncScope();
            var ctx = s.ServiceProvider.GetRequiredService<CommercialDbContext>();
            await ctx.Database.OpenConnectionAsync(); // interceptor -> initializer -> set_config on this connection
            return await SpikeSession.PayloadsAsync(ctx.Database.GetDbConnection());
        }

        var customerX = ExecutionScope.CustomerBinding(
            new PrincipalId(Guid.NewGuid()),
            new ResolvedCustomerBinding(new CustomerBindingId(Guid.NewGuid()), new TenantId(TenantA), new CompanyId(CompanyA1), new CustomerAccountId(CustomerX), PermissionSet.Empty),
            "spike-x");
        var workforceA2 = ExecutionScope.WorkforceCompany(new PrincipalId(Guid.NewGuid()), new TenantId(TenantA), new CompanyId(CompanyA2), PermissionSet.Empty, "spike-w");

        Assert.Equal(["A1-X-1", "A1-X-2"], await ReadAs(customerX));
        Assert.Empty(await ReadAs(ExecutionScope.Unscoped));                                   // unscoped -> nothing (same pooled backend)
        Assert.Empty(await ReadAs(ExecutionScope.SystemInfrastructure("job-1")));             // worker without business metadata -> nothing
        // (SYSTEM "tenant but no company" can no longer be constructed: ExecutionScope v2 fails closed.)
        Assert.Equal(["A2-Z-1"], await ReadAs(workforceA2));
        Assert.Equal(["A1-X-1", "A1-X-2"], await ReadAs(customerX));                           // back again: no residue
    }

    /// <summary>Spike-only initializer: maps ExecutionScope to the RLS session keys, rewriting every key on every open.</summary>
    private sealed class RlsSpikeSessionInitializer : IDbSessionInitializer
    {
        public async ValueTask InitializeAsync(DbConnection connection, ExecutionScope scope, CancellationToken cancellationToken)
        {
            var principal = scope.PrincipalType switch
            {
                PrincipalType.Workforce => "WORKFORCE",
                PrincipalType.Customer => "CUSTOMER",
                PrincipalType.System => "SYSTEM",
                _ => null,
            };
            await SpikeSession.ApplyAsync(
                connection,
                scope.HasBusinessScope
                    ? new SpikeScope(principal, scope.TenantId?.Value, scope.ActiveCompanyId?.Value, scope.CustomerAccountId?.Value)
                    : SpikeScope.None,
                transactionLocal: false);
        }
    }

    private sealed class SpikeRow
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid CompanyId { get; set; }

        public Guid? CustomerAccountId { get; set; }

        public string Payload { get; set; } = "";
    }

    private sealed class SpikeContext(DbContextOptions<SpikeContext> options, Guid tenant) : DbContext(options)
    {
        public DbSet<SpikeRow> Rows => Set<SpikeRow>();

        public static SpikeContext Create(string connectionString, Guid tenant) =>
            new(new DbContextOptionsBuilder<SpikeContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options, tenant);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SpikeRow>(e =>
            {
                e.ToTable("scoped_rows", "spike");
                e.HasQueryFilter(r => r.TenantId == tenant);
            });
        }
    }
}
