using Commercial.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Commercial.UnitTests;

public sealed class PostgresConnectionTests
{
    private const string LocalBase = "Host=localhost;Port=5432;Database=commercial;Username=commercial_platform_app";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static NpgsqlConnectionStringBuilder Runtime(IConfiguration c) => new(PostgresConnection.Resolve(c, DatabaseIdentity.Runtime));

    private static NpgsqlConnectionStringBuilder Migration(IConfiguration c) => new(PostgresConnection.Resolve(c, DatabaseIdentity.Migration));

    [Fact]
    public void Runtime_uses_base_connection_string_by_default()
    {
        var cs = Runtime(Config(("ConnectionStrings:Commercial", LocalBase)));

        Assert.Equal("localhost", cs.Host);
        Assert.Equal("commercial", cs.Database);
        Assert.Equal(DatabaseRoles.Runtime, cs.Username);
        Assert.Null(cs.Password);
    }

    [Fact]
    public void Common_and_runtime_sections_overlay_the_base()
    {
        var cs = Runtime(Config(
            ("ConnectionStrings:Commercial", LocalBase),
            ("Commercial:Postgres:Host", "db.example.invalid"),
            ("Commercial:Postgres:Port", "6543"),
            ("Commercial:Postgres:Database", "commercial_platform_dev"),
            ("Commercial:Postgres:Runtime:Username", DatabaseRoles.Runtime),
            ("Commercial:Postgres:Runtime:Password", "unit-test-only-value")));

        Assert.Equal("db.example.invalid", cs.Host);
        Assert.Equal(6543, cs.Port);
        Assert.Equal("commercial_platform_dev", cs.Database);
        Assert.Equal(DatabaseRoles.Runtime, cs.Username);
        Assert.Equal("unit-test-only-value", cs.Password);
    }

    [Fact]
    public void Migration_uses_its_own_credentials_and_inherits_only_host_port_database()
    {
        var cs = Migration(Config(
            ("ConnectionStrings:Commercial", LocalBase + ";Pass" + "word=runtime-only-value"),
            ("Commercial:Postgres:Host", "db.example.invalid"),
            ("Commercial:Postgres:Runtime:Password", "runtime-only-value"),
            ("Commercial:Postgres:Migration:Username", DatabaseRoles.Owner),
            ("Commercial:Postgres:Migration:Password", "owner-only-value")));

        Assert.Equal("db.example.invalid", cs.Host);
        Assert.Equal("commercial", cs.Database);
        Assert.Equal(DatabaseRoles.Owner, cs.Username);
        Assert.Equal("owner-only-value", cs.Password);
    }

    [Fact]
    public void Migration_never_inherits_the_runtime_password()
    {
        var cs = Migration(Config(
            ("ConnectionStrings:Commercial", LocalBase + ";Pass" + "word=runtime-only-value"),
            ("Commercial:Postgres:Migration:Username", DatabaseRoles.Owner)));

        Assert.Null(cs.Password);
    }

    [Fact]
    public void Migration_requires_an_explicit_identity_and_never_falls_back_to_runtime()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Migration(Config(("ConnectionStrings:Commercial", LocalBase))));
        Assert.Contains("Migration database identity is not configured", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DatabaseRoles.Owner)]
    [InlineData(DatabaseRoles.Maintenance)]
    public void Runtime_rejects_owner_and_maintenance_identities(string username)
    {
        Assert.Throws<InvalidOperationException>(() => Runtime(Config(
            ("ConnectionStrings:Commercial", LocalBase),
            ("Commercial:Postgres:Runtime:Username", username))));
    }

    [Fact]
    public void Runtime_rejects_the_configured_migration_username()
    {
        Assert.Throws<InvalidOperationException>(() => Runtime(Config(
            ("ConnectionStrings:Commercial", "Host=h;Database=d;Username=custom_migrator"),
            ("Commercial:Postgres:Migration:Username", "custom_migrator"))));
    }

    [Theory]
    [InlineData(DatabaseRoles.Runtime)]
    [InlineData(DatabaseRoles.Maintenance)]
    public void Migration_rejects_runtime_and_maintenance_identities(string username)
    {
        Assert.Throws<InvalidOperationException>(() => Migration(Config(
            ("ConnectionStrings:Commercial", LocalBase),
            ("Commercial:Postgres:Migration:Username", username))));
    }

    [Fact]
    public void Maintenance_and_legacy_keys_are_never_selected_by_the_resolver()
    {
        var cs = Runtime(Config(
            ("ConnectionStrings:Commercial", LocalBase),
            ("Commercial:Postgres:Maintenance:Username", DatabaseRoles.Maintenance),
            ("Commercial:Postgres:Maintenance:Password", "maintenance-only-value"),
            ("Commercial:Postgres:Username", "legacy_user"),
            ("Commercial:Postgres:Password", "legacy-value")));

        Assert.Equal(DatabaseRoles.Runtime, cs.Username);
        Assert.Null(cs.Password);
    }

    [Fact]
    public void Missing_configuration_fails_without_leaking_secrets()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PostgresConnection.Resolve(Config(("Commercial:Postgres:Runtime:Password", "unit-test-only-value"))));

        Assert.DoesNotContain("unit-test-only-value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_Reset_On_Close_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PostgresConnection.Resolve(Config(
            ("ConnectionStrings:Commercial", LocalBase + ";No Reset On Close=true"))));
        Assert.Contains("No Reset On Close", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_redacts_the_password() =>
        Assert.DoesNotContain(
            "unit-test-only-value",
            PostgresConnection.Describe("Host=h;Database=d;Username=u;Pass" + "word=unit-test-only-value"),
            StringComparison.Ordinal);
}
