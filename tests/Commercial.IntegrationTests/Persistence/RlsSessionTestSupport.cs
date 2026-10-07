using System.Data.Common;
using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Abstractions.Security;
using Commercial.Domain.Identity;
using Commercial.Infrastructure.Persistence;
using Commercial.Infrastructure.Persistence.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.IntegrationTests.Persistence;

/// <summary>Scopes and helpers shared by the WS3 session-context tests (runtime identity only).</summary>
internal static class RlsSession
{
    public static readonly TenantId TenantA = new(Guid.Parse("a0000000-0000-0000-0000-00000000000a"));
    public static readonly TenantId TenantB = new(Guid.Parse("b0000000-0000-0000-0000-00000000000b"));
    public static readonly CompanyId CompanyA1 = new(Guid.Parse("a1000000-0000-0000-0000-0000000000a1"));
    public static readonly CompanyId CompanyA2 = new(Guid.Parse("a2000000-0000-0000-0000-0000000000a2"));
    public static readonly CompanyId CompanyB1 = new(Guid.Parse("b1000000-0000-0000-0000-0000000000b1"));
    public static readonly CustomerAccountId AccountX = new(Guid.Parse("c0000000-0000-0000-0000-00000000000c"));
    public static readonly CustomerAccountId AccountY = new(Guid.Parse("d0000000-0000-0000-0000-00000000000d"));
    public static readonly CustomerBindingId BindingX = new(Guid.Parse("e0000000-0000-0000-0000-00000000000e"));
    public static readonly CustomerBindingId BindingY = new(Guid.Parse("f0000000-0000-0000-0000-00000000000f"));
    public static readonly PrincipalId Principal = new(Guid.Parse("90000000-0000-0000-0000-000000000009"));

    public static ExecutionScope WorkforceA1(string correlation = "wf-a1") =>
        ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, PermissionSet.Of(SecurityPermissions.CompanyAdminister), correlation);

    public static ExecutionScope WorkforceA2(string correlation = "wf-a2") =>
        ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA2, PermissionSet.Empty, correlation);

    public static ExecutionScope WorkforceB1(string correlation = "wf-b1") =>
        ExecutionScope.WorkforceCompany(Principal, TenantB, CompanyB1, PermissionSet.Empty, correlation);

    public static ExecutionScope WorkforceTenantWideA(string correlation = "wf-tw") =>
        ExecutionScope.WorkforceTenant(Principal, TenantA, ScopeGrants.TenantWideCompanyAccess, PermissionSet.Of(SecurityPermissions.TenantAdminister), correlation);

    public static ExecutionScope CustomerX(string correlation = "cu-x") =>
        ExecutionScope.CustomerBinding(Principal, new ResolvedCustomerBinding(BindingX, TenantA, CompanyA1, AccountX, PermissionSet.Of(SecurityPermissions.CustomerUsersManage)), correlation);

    public static ExecutionScope CustomerY(string correlation = "cu-y") =>
        ExecutionScope.CustomerBinding(Principal, new ResolvedCustomerBinding(BindingY, TenantA, CompanyA1, AccountY, PermissionSet.Of(SecurityPermissions.CustomerUsersRead)), correlation);

    public static ExecutionScope SystemB1(string correlation = "sys-b1") =>
        ExecutionScope.SystemBusiness(TenantB, CompanyB1, null, null, correlation, "job-1");

    public static ExecutionScope Identity(string correlation = "identity") =>
        ExecutionScope.ForIdentity(new AuthenticatedIdentityContext("https://idp.test", "sub-1", PrincipalType.Customer, correlation), Principal);

    /// <summary>Runs <paramref name="work"/> with a fresh DbContext created and used under <paramref name="scope"/>.</summary>
    public static async Task<T> InScopeAsync<T>(IServiceProvider provider, ExecutionScope scope, Func<CommercialDbContext, Task<T>> work)
    {
        using var ambient = provider.GetRequiredService<IExecutionScopeAccessor>().Begin(scope);
        await using var di = provider.CreateAsyncScope();
        return await work(di.ServiceProvider.GetRequiredService<CommercialDbContext>());
    }

    /// <summary>Opens through EF (initializer path) and returns the PostgreSQL session keys + backend pid.</summary>
    public static async Task<(IReadOnlyDictionary<string, string?> Keys, int Pid)> SessionAsync(CommercialDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        return (await DbSessionContextReader.ReadAllAsync(connection, CancellationToken.None), await BackendPidAsync(connection));
    }

    public static Task<(IReadOnlyDictionary<string, string?> Keys, int Pid)> SessionAsync(IServiceProvider provider, ExecutionScope scope) =>
        InScopeAsync(provider, scope, SessionAsync);

    public static async Task<int> BackendPidAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_backend_pid()";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Every key equals the one-way mapping of <paramref name="scope"/> — nothing more, nothing stale.</summary>
    public static void AssertExactly(ExecutionScope scope, IReadOnlyDictionary<string, string?> actual)
    {
        var expected = DbSessionContext.FromScope(scope);
        Assert.Equal(DbSessionContext.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, value) in expected.Settings)
        {
            Assert.True(string.Equals(value, actual[key], StringComparison.Ordinal), $"{key}: expected '{value}', actual '{actual[key]}'");
        }
    }

    public static DbSessionScopeException SessionFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbSessionScopeException session)
            {
                return session;
            }
        }

        throw new Xunit.Sdk.XunitException($"expected DbSessionScopeException, got {ex.GetType().Name}: {ex.Message}");
    }

    public static readonly string[] BusinessKeys =
    [
        DbSessionContext.TenantId, DbSessionContext.CompanyId, DbSessionContext.CustomerAccountId, DbSessionContext.CustomerBindingId,
    ];
}
