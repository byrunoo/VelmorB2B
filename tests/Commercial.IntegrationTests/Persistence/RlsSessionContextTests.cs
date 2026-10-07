using System.Runtime.CompilerServices;
using Commercial.Application.Abstractions.Messaging;
using Commercial.Application.Abstractions.Scope;
using Commercial.Infrastructure.Messaging.Outbox;
using Commercial.Infrastructure.Persistence;
using Commercial.Infrastructure.Persistence.Session;
using Commercial.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Commercial.IntegrationTests.Persistence.RlsSession;

namespace Commercial.IntegrationTests.Persistence;

/// <summary>
/// Phase 1 WS3 — production <see cref="RlsDbSessionInitializer"/> (writer) and <see cref="DbSessionScopeGuard"/>
/// (verifier) against real PostgreSQL, runtime identity <c>commercial_platform_app</c>, Npgsql pooling ON.
/// </summary>
[Trait("ADR-0001", "session-context")]
public sealed class RlsSessionContextTests(ContainersFixture containers)
{
    private async Task<ServiceProvider> ProviderAsync(string? options = null) =>
        containers.BuildServices(await containers.CreateMigratedDatabaseAsync(options));

    // ---------- 1–9: keys written ----------

    public static TheoryData<string> BusinessScopes => ["workforce", "customer", "system-business", "workforce-tenant-wide"];

    private static ExecutionScope Named(string name) => name switch
    {
        "workforce" => WorkforceA1(),
        "customer" => CustomerX(),
        "system-business" => SystemB1(),
        "workforce-tenant-wide" => WorkforceTenantWideA(),
        "anonymous" => ExecutionScope.Anonymous("anon"),
        "identity" => Identity(),
        "system-infrastructure" => ExecutionScope.SystemInfrastructure("sys-infra"),
        "unscoped" => ExecutionScope.Unscoped,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory] // 1, 2, 3
    [MemberData(nameof(BusinessScopes))]
    public async Task All_keys_are_written_for_business_scopes(string name)
    {
        await using var provider = await ProviderAsync();
        var scope = Named(name);

        var (keys, _) = await SessionAsync(provider, scope);

        AssertExactly(scope, keys);
        Assert.Equal(scope.TenantId!.Value.ToString(), keys[DbSessionContext.TenantId]);
        Assert.Equal(PrincipalTypeValues.ToWire(scope.PrincipalType), keys[DbSessionContext.PrincipalType]);
        Assert.Equal(scope.Kind.ToString(), keys[DbSessionContext.ScopeKind]);
    }

    [Theory] // 4
    [InlineData("anonymous")]
    [InlineData("identity")]
    [InlineData("system-infrastructure")]
    [InlineData("unscoped")]
    public async Task Non_business_scopes_write_empty_business_keys(string name)
    {
        await using var provider = await ProviderAsync();
        var scope = Named(name);

        var (keys, _) = await SessionAsync(provider, scope);

        AssertExactly(scope, keys);
        Assert.All(BusinessKeys, k => Assert.Equal(string.Empty, keys[k])); // written as '', not left unset
        Assert.Equal("false", keys[DbSessionContext.TenantWideCompanyAccess]);
    }

    [Fact] // 5, 6
    public async Task Principal_and_binding_ids_are_written_exactly()
    {
        await using var provider = await ProviderAsync();

        var (keys, _) = await SessionAsync(provider, CustomerX());

        Assert.Equal(Principal.ToString(), keys[DbSessionContext.PrincipalId]);
        Assert.Equal(BindingX.ToString(), keys[DbSessionContext.CustomerBindingId]);
        Assert.Equal(AccountX.ToString(), keys[DbSessionContext.CustomerAccountId]);
        Assert.Equal(CompanyA1.ToString(), keys[DbSessionContext.CompanyId]);
        Assert.Equal("CUSTOMER", keys[DbSessionContext.PrincipalType]);
    }

    [Fact] // 7, 8
    public async Task Tenant_wide_flag_only_for_the_explicit_workforce_grant()
    {
        await using var provider = await ProviderAsync();

        Assert.Equal("true", (await SessionAsync(provider, WorkforceTenantWideA())).Keys[DbSessionContext.TenantWideCompanyAccess]);
        Assert.Equal(string.Empty, (await SessionAsync(provider, WorkforceTenantWideA())).Keys[DbSessionContext.CompanyId]);
        Assert.Equal("false", (await SessionAsync(provider, WorkforceA1())).Keys[DbSessionContext.TenantWideCompanyAccess]); // company scope: no grant
        Assert.Equal("false", (await SessionAsync(provider, CustomerX())).Keys[DbSessionContext.TenantWideCompanyAccess]);   // CUSTOMER never
        Assert.Equal("false", (await SessionAsync(provider, SystemB1())).Keys[DbSessionContext.TenantWideCompanyAccess]);    // SYSTEM never
    }

    [Fact] // 9
    public async Task Fingerprint_is_written_exactly()
    {
        await using var provider = await ProviderAsync();
        var scope = WorkforceA1();

        var (keys, _) = await SessionAsync(provider, scope);

        Assert.Equal(scope.Fingerprint.Value, keys[DbSessionContext.ScopeFingerprint]);
    }

    // ---------- 10–12, 19, 20: pooled reuse ----------

    [Fact] // 10
    [Trait("ADR-0001", "pool-scope-leak")]
    public async Task Pooled_reuse_tenant_A_then_tenant_B_keeps_no_A_value()
    {
        await using var provider = await ProviderAsync("Maximum Pool Size=1");

        var a = await SessionAsync(provider, WorkforceA1());
        var b = await SessionAsync(provider, WorkforceB1());

        Assert.Equal(a.Pid, b.Pid); // same physical connection
        AssertExactly(WorkforceB1(), b.Keys);
        Assert.DoesNotContain(b.Keys.Values, v => v == TenantA.ToString() || v == CompanyA1.ToString());
    }

    [Fact] // 11
    public async Task Pooled_reuse_customer_binding_X_then_Y()
    {
        await using var provider = await ProviderAsync("Maximum Pool Size=1");

        var x = await SessionAsync(provider, CustomerX());
        var y = await SessionAsync(provider, CustomerY());

        Assert.Equal(x.Pid, y.Pid);
        AssertExactly(CustomerY(), y.Keys);
        Assert.Equal(BindingY.ToString(), y.Keys[DbSessionContext.CustomerBindingId]);
        Assert.Equal(AccountY.ToString(), y.Keys[DbSessionContext.CustomerAccountId]);
    }

    [Fact] // 12
    public async Task Business_then_non_business_reuse_clears_business_keys()
    {
        await using var provider = await ProviderAsync("Maximum Pool Size=1");

        var business = await SessionAsync(provider, CustomerX());
        var infra = await SessionAsync(provider, ExecutionScope.SystemInfrastructure("after-customer"));

        Assert.Equal(business.Pid, infra.Pid);
        Assert.All(BusinessKeys, k => Assert.Equal(string.Empty, infra.Keys[k]));
        Assert.Equal("SystemInfrastructure", infra.Keys[DbSessionContext.ScopeKind]);
    }

    [Theory] // 19, 20
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transaction_end_then_pooled_reuse_has_no_leakage(bool commit)
    {
        await using var provider = await ProviderAsync("Maximum Pool Size=1");

        var first = await InScopeAsync(provider, WorkforceA1(), async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var pid = await BackendPidAsync(db.Database.GetDbConnection());
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO commercial.diagnostic_ping_receipts (message_id, correlation_id, recorded_at_utc) VALUES ({0}, 'tx', now())", Guid.NewGuid());
            if (commit)
            {
                await tx.CommitAsync();
            }
            else
            {
                await tx.RollbackAsync();
            }

            return pid;
        });

        var next = await SessionAsync(provider, CustomerY());

        Assert.Equal(first, next.Pid);
        AssertExactly(CustomerY(), next.Keys);
    }

    // ---------- 13–15: fail closed ----------

    [Fact] // 13
    [Trait("ADR-0001", "fail-closed")]
    public async Task Connection_opened_outside_the_initializer_cannot_run_commands()
    {
        await using var provider = await ProviderAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => InScopeAsync(provider, WorkforceA1(), async db =>
        {
            await db.Database.GetDbConnection().OpenAsync(); // bypasses EF → no initializer, no recorded fingerprint
            return await db.OutboxMessages.CountAsync();
        }));

        Assert.Equal(DbSessionScopeException.NotInitialized, SessionFailure(ex).Code);
    }

    [Fact] // 14
    [Trait("ADR-0001", "fail-closed")]
    public async Task Invalid_scope_cannot_initialize_and_leaves_no_usable_connection()
    {
        await using var provider = await ProviderAsync();
        var invalid = (ExecutionScope)RuntimeHelpers.GetUninitializedObject(typeof(ExecutionScope)); // bypasses the factories

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => InScopeAsync(provider, invalid, async db =>
        {
            await db.Database.OpenConnectionAsync();
            return 0;
        }));
        Assert.Equal(DbSessionScopeException.InitializationFailed, SessionFailure(ex).Code);

        // A valid scope afterwards gets a clean, fully written session.
        AssertExactly(WorkforceA1(), (await SessionAsync(provider, WorkforceA1())).Keys);
    }

    [Fact] // 15
    public async Task No_Reset_On_Close_is_rejected_by_configuration_and_at_runtime()
    {
        var cs = await containers.CreateMigratedDatabaseAsync("No Reset On Close=true");

        // Configuration guard (resolver).
        await using (var provider = containers.BuildServices(cs))
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CommercialDbContext>().Database.OpenConnectionAsync();
            });
        }

        // Runtime guard (initializer), even if a context is built around the resolver.
        await using var sane = await ProviderAsync();
        var options = new DbContextOptionsBuilder<CommercialDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sane.GetRequiredService<DbSessionInitializerInterceptor>(), sane.GetRequiredService<DbSessionScopeGuardInterceptor>())
            .Options;
        await using var db = new CommercialDbContext(options);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.OpenConnectionAsync());
        Assert.Equal(DbSessionScopeException.InitializationFailed, SessionFailure(ex).Code);
    }

    // ---------- 16–18, 21, 22: fingerprint guard ----------

    [Fact] // 16, 22
    public async Task Open_connection_under_A_then_ambient_B_rejects_EF_LINQ()
    {
        await using var provider = await ProviderAsync();
        var accessor = provider.GetRequiredService<IExecutionScopeAccessor>();

        using (accessor.Begin(WorkforceA1()))
        {
            await using var di = provider.CreateAsyncScope();
            var db = di.ServiceProvider.GetRequiredService<CommercialDbContext>();
            await db.Database.OpenConnectionAsync();
            Assert.Equal(0, await db.OutboxMessages.CountAsync()); // A on A: allowed

            using (accessor.Begin(WorkforceB1()))
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.OutboxMessages.CountAsync());
                Assert.Equal(DbSessionScopeException.ScopeMismatch, SessionFailure(ex).Code);
            }

            Assert.Equal(0, await db.OutboxMessages.CountAsync()); // back under A: the session was never switched in place
        }
    }

    [Fact] // 17
    public async Task Same_security_context_is_allowed_even_with_a_different_correlation()
    {
        await using var provider = await ProviderAsync();
        var accessor = provider.GetRequiredService<IExecutionScopeAccessor>();

        using (accessor.Begin(CustomerX("first-request")))
        {
            await using var di = provider.CreateAsyncScope();
            var db = di.ServiceProvider.GetRequiredService<CommercialDbContext>();
            await db.Database.OpenConnectionAsync();

            using (accessor.Begin(CustomerX("same-scope-other-correlation")))
            {
                Assert.Equal(0, await db.OutboxMessages.CountAsync());
            }
        }
    }

    [Fact] // 18
    public async Task Transaction_started_under_A_rejects_SQL_under_B()
    {
        await using var provider = await ProviderAsync();
        var accessor = provider.GetRequiredService<IExecutionScopeAccessor>();

        using (accessor.Begin(CustomerX()))
        {
            await using var di = provider.CreateAsyncScope();
            var db = di.ServiceProvider.GetRequiredService<CommercialDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT 1");

            using (accessor.Begin(CustomerY()))
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("SELECT 1"));
                Assert.Equal(DbSessionScopeException.ScopeMismatch, SessionFailure(ex).Code);
            }

            await tx.RollbackAsync();
        }
    }

    [Fact] // 21
    public async Task Raw_SQL_through_EF_passes_the_guard()
    {
        await using var provider = await ProviderAsync();
        var accessor = provider.GetRequiredService<IExecutionScopeAccessor>();

        using (accessor.Begin(SystemB1()))
        {
            await using var di = provider.CreateAsyncScope();
            var db = di.ServiceProvider.GetRequiredService<CommercialDbContext>();
            await db.Database.OpenConnectionAsync(); // keep this connection (EF would otherwise close it after each query)
            Assert.Equal(["SystemBusiness"], await db.Database.SqlQueryRaw<string>("SELECT current_setting('commercial.scope_kind', true) AS \"Value\"").ToListAsync());

            using (accessor.Begin(ExecutionScope.SystemInfrastructure("other")))
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").ToListAsync());
                Assert.Equal(DbSessionScopeException.ScopeMismatch, SessionFailure(ex).Code);
            }
        }
    }

    [Theory]
    [InlineData("SELECT set_config('commercial.tenant_id', '', false)")]
    [InlineData("SELECT set_config({0}, 'x', false)")]
    [InlineData("SET commercial.company_id = 'x'")]
    [InlineData("SET LOCAL commercial.tenant_id TO 'x'")]
    [InlineData("RESET ALL")]
    [InlineData("DISCARD ALL")]
    [InlineData("SET ROLE commercial_platform_owner")]
    public async Task Application_SQL_cannot_write_session_keys(string sql)
    {
        await using var provider = await ProviderAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => InScopeAsync(provider, WorkforceA1(),
            db => db.Database.ExecuteSqlRawAsync(sql, "commercial.tenant_id")));

        Assert.Equal(DbSessionScopeException.KeyWriteForbidden, SessionFailure(ex).Code);
    }

    [Fact]
    public async Task Raw_ADO_infrastructure_path_is_verified_too()
    {
        await using var provider = await ProviderAsync();
        var accessor = provider.GetRequiredService<IExecutionScopeAccessor>();

        using (accessor.Begin(ExecutionScope.SystemInfrastructure("claim")))
        {
            await using var di = provider.CreateAsyncScope();
            var store = di.ServiceProvider.GetRequiredService<IOutboxStore>();
            Assert.Empty(await store.ClaimAsync("ws3", 10, TimeSpan.FromSeconds(30), CancellationToken.None));

            using (accessor.Begin(WorkforceA1()))
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => store.ClaimAsync("ws3", 10, TimeSpan.FromSeconds(30), CancellationToken.None));
                Assert.Equal(DbSessionScopeException.ScopeMismatch, SessionFailure(ex).Code);
            }
        }
    }

    // ---------- 23–25: lifecycle ----------

    [Fact] // 23
    public async Task Manually_opened_EF_connection_is_initialized_sync_and_async()
    {
        await using var provider = await ProviderAsync();

        var syncKeys = await InScopeAsync(provider, CustomerX(), async db =>
        {
            db.Database.OpenConnection(); // synchronous interceptor path
            Assert.Equal(0, db.OutboxMessages.Count());
            return await DbSessionContextReader.ReadAllAsync(db.Database.GetDbConnection(), CancellationToken.None);
        });
        AssertExactly(CustomerX(), syncKeys);

        AssertExactly(WorkforceA2(), (await SessionAsync(provider, WorkforceA2())).Keys);
    }

    [Fact] // 24
    public async Task Reconnect_under_a_new_scope_reinitializes_every_key()
    {
        await using var provider = await ProviderAsync();
        var accessor = provider.GetRequiredService<IExecutionScopeAccessor>();

        await using var di = provider.CreateAsyncScope();
        var db = di.ServiceProvider.GetRequiredService<CommercialDbContext>();
        using (accessor.Begin(CustomerX()))
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.CloseConnectionAsync();
        }

        using (accessor.Begin(WorkforceB1()))
        {
            var (keys, _) = await SessionAsync(db);
            AssertExactly(WorkforceB1(), keys);
            Assert.Equal(0, await db.OutboxMessages.CountAsync()); // guard accepts the re-initialized session
        }
    }

    [Fact] // 25
    public async Task Broken_connection_reopen_does_not_retain_stale_scope()
    {
        var database = await containers.CreateDatabaseAsync("Maximum Pool Size=1");
        await using var provider = containers.BuildServices(database.RuntimeConnectionString);
        var accessor = provider.GetRequiredService<IExecutionScopeAccessor>();

        await using var di = provider.CreateAsyncScope();
        var db = di.ServiceProvider.GetRequiredService<CommercialDbContext>();
        int stalePid;
        using (accessor.Begin(CustomerX()))
        {
            (_, stalePid) = await SessionAsync(db);
        }

        await using (var admin = new NpgsqlConnection(database.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin);
            kill.Parameters.AddWithValue("pid", stalePid);
            await kill.ExecuteScalarAsync();
        }

        using (accessor.Begin(WorkforceA1()))
        {
            // The open connection belongs to scope X: rejected before reaching the (dead) server.
            var mismatch = await Assert.ThrowsAnyAsync<Exception>(() => db.OutboxMessages.CountAsync());
            Assert.Equal(DbSessionScopeException.ScopeMismatch, SessionFailure(mismatch).Code);

            // Recovery = close + reopen. The pooled connector is dead: the first re-initialization may fail (fail closed,
            // Npgsql discards the broken connector); the next open is a new physical connection initialized for the current scope.
            await db.Database.CloseConnectionAsync();
            (IReadOnlyDictionary<string, string?> Keys, int Pid) session;
            try
            {
                session = await SessionAsync(db);
            }
            catch (Exception ex) when (ex is NpgsqlException || ex.InnerException is NpgsqlException)
            {
                await db.Database.CloseConnectionAsync();
                session = await SessionAsync(db);
            }

            var (keys, pid) = session;
            Assert.NotEqual(stalePid, pid);
            AssertExactly(WorkforceA1(), keys);
            Assert.Equal(0, await db.OutboxMessages.CountAsync());
        }
    }

    [Fact]
    public async Task Execution_strategy_retry_with_reopen_initializes_the_current_scope()
    {
        await using var provider = await ProviderAsync("Maximum Pool Size=1");
        var accessor = provider.GetRequiredService<IExecutionScopeAccessor>();
        var attempts = 0;

        using (accessor.Begin(CustomerY()))
        {
            await using var di = provider.CreateAsyncScope();
            var db = di.ServiceProvider.GetRequiredService<CommercialDbContext>();
            var keys = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                attempts++;
                await db.Database.OpenConnectionAsync();
                var values = await DbSessionContextReader.ReadAllAsync(db.Database.GetDbConnection(), CancellationToken.None);
                await db.Database.CloseConnectionAsync(); // what a retrying strategy does between attempts
                await db.Database.OpenConnectionAsync();
                Assert.Equal(0, await db.OutboxMessages.CountAsync());
                return values;
            });

            Assert.Equal(1, attempts);
            AssertExactly(CustomerY(), keys);
        }
    }

    [Fact]
    public async Task Health_check_style_probe_needs_no_business_scope()
    {
        await using var provider = await ProviderAsync();
        await using var di = provider.CreateAsyncScope(); // no ambient scope at all (Unscoped)

        Assert.True(await di.ServiceProvider.GetRequiredService<CommercialDbContext>().Database.CanConnectAsync());
    }
}
