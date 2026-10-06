using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Commercial.Infrastructure.Persistence;

/// <summary>
/// The SINGLE place the Commercial PostgreSQL connection string is resolved (DbContext, Hangfire
/// storage and design-time tooling all use it).
///
/// 1. Base: <c>ConnectionStrings:Commercial</c> (appsettings / env <c>ConnectionStrings__Commercial</c>).
/// 2. Overlay: any non-empty part of section <c>Commercial:Postgres</c> —
///    <c>Host, Port, Database, Username, Password</c> (env e.g. <c>COMMERCIAL__POSTGRES__PASSWORD</c>,
///    or user-secrets key <c>Commercial:Postgres:Password</c>).
///
/// Passwords are supplied only through environment variables / user-secrets / a secret store —
/// never through tracked files. Error messages never contain the password.
/// </summary>
public static class PostgresConnection
{
    public const string SectionName = "Commercial:Postgres";

    public static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var builder = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName) ?? string.Empty);
        var section = configuration.GetSection(SectionName);

        if (Value(section, "Host") is { } host)
        {
            builder.Host = host;
        }

        if (Value(section, "Port") is { } port)
        {
            builder.Port = int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p)
                ? p
                : throw new InvalidOperationException($"'{SectionName}:Port' must be a number.");
        }

        if (Value(section, "Database") is { } database)
        {
            builder.Database = database;
        }

        if (Value(section, "Username") is { } username)
        {
            builder.Username = username;
        }

        if (Value(section, "Password") is { } password)
        {
            builder.Password = password;
        }

        if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
        {
            throw new InvalidOperationException(
                $"PostgreSQL is not configured. Set 'ConnectionStrings:{PersistenceRegistration.ConnectionStringName}' " +
                $"and/or '{SectionName}:Host|Port|Database|Username', with the password from an environment variable " +
                "(COMMERCIAL__POSTGRES__PASSWORD) or user-secrets. Never commit credentials.");
        }

        builder.PersistSecurityInfo = false;
        return builder.ConnectionString;
    }

    /// <summary>Connection description safe for logs/reports (no password).</summary>
    public static string Describe(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        return $"Host={b.Host};Port={b.Port};Database={b.Database};Username={b.Username};Password={(string.IsNullOrEmpty(b.Password) ? "<none>" : "<redacted>")}";
    }

    private static string? Value(IConfiguration section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
