using System.Runtime.CompilerServices;
using Commercial.Application.Abstractions.Scope;
using Commercial.Infrastructure.Persistence.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Commercial.UnitTests.Security;

/// <summary>Phase 1 WS3 — database-free parts of the session writer / verifier (the real behaviour is proven in the integration tests).</summary>
public sealed class DbSessionGuardTests
{
    private static DbSessionScopeGuard Guard() =>
        new(new AmbientExecutionScopeAccessor(), new DbSessionRegistry(), NullLogger<DbSessionScopeGuard>.Instance);

    [Theory]
    [InlineData("SELECT set_config('commercial.tenant_id', '', false)")]
    [InlineData("select SET_CONFIG(@k0, @v0, false)")]
    [InlineData("SET commercial.tenant_id = 'x'")]
    [InlineData("SET SESSION commercial.company_id TO 'x'")]
    [InlineData("SET LOCAL commercial.tenant_id = 'x'")]
    [InlineData("RESET commercial.tenant_id")]
    [InlineData("RESET ALL")]
    [InlineData("DISCARD ALL")]
    [InlineData("SET ROLE commercial_platform_owner")]
    [InlineData("SET SESSION AUTHORIZATION commercial_platform_owner")]
    public void Session_key_mutations_are_rejected(string sql)
    {
        using var command = new NpgsqlCommand(sql);
        var ex = Assert.Throws<DbSessionScopeException>(() => Guard().Verify(command));
        Assert.Equal(DbSessionScopeException.KeyWriteForbidden, ex.Code);
    }

    [Theory]
    [InlineData("SELECT o.id FROM commercial.outbox_messages AS o")]
    [InlineData("UPDATE commercial.role_assignments SET role = @p0 WHERE id = @p1")]
    [InlineData("UPDATE t SET reset_at = now(), discarded = true")]
    [InlineData("INSERT INTO t (setting) VALUES ('commercial.x')")]
    public void Ordinary_SQL_is_not_mistaken_for_a_session_write_but_still_needs_an_initialized_session(string sql)
    {
        using var command = new NpgsqlCommand(sql); // no connection => never initialized
        var ex = Assert.Throws<DbSessionScopeException>(() => Guard().Verify(command));
        Assert.Equal(DbSessionScopeException.NotInitialized, ex.Code);
    }

    [Fact]
    public void Writer_rejects_missing_invalid_scopes_and_disabled_pool_reset_before_touching_the_database()
    {
        using var sane = new NpgsqlConnection("Host=unit.invalid;Username=commercial_platform_app");
        using var noReset = new NpgsqlConnection("Host=unit.invalid;Username=commercial_platform_app;No Reset On Close=true");
        var uninitialized = (ExecutionScope)RuntimeHelpers.GetUninitializedObject(typeof(ExecutionScope));

        Assert.Equal(DbSessionScopeException.InitializationFailed, Assert.Throws<DbSessionScopeException>(() => RlsDbSessionInitializer.Prepare(sane, null)).Code);
        Assert.Equal(DbSessionScopeException.InitializationFailed, Assert.Throws<DbSessionScopeException>(() => RlsDbSessionInitializer.Prepare(sane, uninitialized)).Code);
        Assert.Equal(DbSessionScopeException.InitializationFailed,
            Assert.Throws<DbSessionScopeException>(() => RlsDbSessionInitializer.Prepare(noReset, ExecutionScope.Unscoped)).Code);

        var context = RlsDbSessionInitializer.Prepare(sane, ExecutionScope.SystemInfrastructure("ok"));
        Assert.Equal(DbSessionContext.Keys, context.Settings.Select(s => s.Key));
    }
}
