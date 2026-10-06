using Commercial.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Commercial.UnitTests;

public sealed class PostgresConnectionTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Base_connection_string_is_used_when_no_overrides()
    {
        var cs = new NpgsqlConnectionStringBuilder(PostgresConnection.Resolve(Config(
            ("ConnectionStrings:Commercial", "Host=localhost;Port=5432;Database=commercial;Username=commercial"))));

        Assert.Equal("localhost", cs.Host);
        Assert.Equal("commercial", cs.Database);
        Assert.Null(cs.Password);
    }

    [Fact]
    public void Section_parts_overlay_the_base_and_empty_values_are_ignored()
    {
        var cs = new NpgsqlConnectionStringBuilder(PostgresConnection.Resolve(Config(
            ("ConnectionStrings:Commercial", "Host=localhost;Port=5432;Database=commercial;Username=commercial"),
            ("Commercial:Postgres:Host", "db.example.invalid"),
            ("Commercial:Postgres:Port", "6543"),
            ("Commercial:Postgres:Database", "commercial_platform_dev"),
            ("Commercial:Postgres:Username", "commercial_platform_app"),
            ("Commercial:Postgres:Password", "unit-test-only-value"))));

        Assert.Equal("db.example.invalid", cs.Host);
        Assert.Equal(6543, cs.Port);
        Assert.Equal("commercial_platform_dev", cs.Database);
        Assert.Equal("commercial_platform_app", cs.Username);
        Assert.Equal("unit-test-only-value", cs.Password);

        var blankOverrides = new NpgsqlConnectionStringBuilder(PostgresConnection.Resolve(Config(
            ("ConnectionStrings:Commercial", "Host=localhost;Database=commercial;Username=commercial"),
            ("Commercial:Postgres:Host", ""),
            ("Commercial:Postgres:Password", " "))));
        Assert.Equal("localhost", blankOverrides.Host);
        Assert.Null(blankOverrides.Password);
    }

    [Fact]
    public void Missing_configuration_fails_without_leaking_secrets()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PostgresConnection.Resolve(Config(("Commercial:Postgres:Password", "unit-test-only-value"))));

        Assert.DoesNotContain("unit-test-only-value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_redacts_the_password() =>
        Assert.DoesNotContain(
            "unit-test-only-value",
            PostgresConnection.Describe("Host=h;Database=d;Username=u;Pass" + "word=unit-test-only-value"),
            StringComparison.Ordinal);
}
