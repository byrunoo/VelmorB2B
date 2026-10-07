using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Commercial.Infrastructure.Persistence;

/// <summary>ADR-0001 database roles (identical names in local, shared-dev, Testcontainers and production).</summary>
public static class DatabaseRoles
{
    /// <summary>Migrations, schema/table/function ownership, Hangfire schema provisioning. Never runtime.</summary>
    public const string Owner = "commercial_platform_owner";

    /// <summary>API + Worker runtime: DML only, owns nothing, NOBYPASSRLS.</summary>
    public const string Runtime = "commercial_platform_app";

    /// <summary>Controlled operations (may BYPASSRLS). Never part of application configuration.</summary>
    public const string Maintenance = "commercial_platform_maintenance";
}

/// <summary>Which database identity a connection is resolved for.</summary>
public enum DatabaseIdentity
{
    Runtime,
    Migration,
}

/// <summary>
/// The SINGLE place Commercial PostgreSQL connection strings are resolved (runtime DbContext, Hangfire storage,
/// database provisioning and design-time tooling all use it).
///
/// 1. Base: <c>ConnectionStrings:Commercial</c> (appsettings / env <c>ConnectionStrings__Commercial</c>).
/// 2. Common overlay: non-empty <c>Commercial:Postgres:Host|Port|Database</c>.
/// 3. Identity overlay: non-empty <c>Commercial:Postgres:Runtime:Username|Password</c> or
///    <c>Commercial:Postgres:Migration:Username|Password</c>
///    (env e.g. <c>COMMERCIAL__POSTGRES__RUNTIME__PASSWORD</c>, <c>COMMERCIAL__POSTGRES__MIGRATION__PASSWORD</c>).
///
/// Identity separation (ADR-0001):
///  * runtime never resolves the owner or maintenance role and never falls back to migration settings;
///  * migration requires an explicitly configured migration username and never falls back to the runtime one;
///  * there is no maintenance identity here at all — maintenance is not application configuration.
///
/// Passwords come only from environment variables / user-secrets / a secret store — never tracked files.
/// Error messages never contain passwords.
/// </summary>
public static class PostgresConnection
{
    public const string SectionName = "Commercial:Postgres";
    public const string RuntimeSectionName = SectionName + ":Runtime";
    public const string MigrationSectionName = SectionName + ":Migration";

    /// <summary>Runtime (API / Worker) connection — the <see cref="DatabaseRoles.Runtime"/> identity.</summary>
    public static string Resolve(IConfiguration configuration) => Resolve(configuration, DatabaseIdentity.Runtime);

    public static string Resolve(IConfiguration configuration, DatabaseIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var builder = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName) ?? string.Empty);
        var common = configuration.GetSection(SectionName);

        if (Value(common, "Host") is { } host)
        {
            builder.Host = host;
        }

        if (Value(common, "Port") is { } port)
        {
            builder.Port = int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p)
                ? p
                : throw new InvalidOperationException($"'{SectionName}:Port' must be a number.");
        }

        if (Value(common, "Database") is { } database)
        {
            builder.Database = database;
        }

        if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
        {
            throw new InvalidOperationException(
                $"PostgreSQL is not configured. Set 'ConnectionStrings:{PersistenceRegistration.ConnectionStringName}' and/or " +
                $"'{SectionName}:Host|Port|Database', '{RuntimeSectionName}:Username|Password' and (for migrations) " +
                $"'{MigrationSectionName}:Username|Password'. Passwords only via environment variables / user-secrets.");
        }

        var runtimeSection = configuration.GetSection(RuntimeSectionName);
        var migrationSection = configuration.GetSection(MigrationSectionName);
        var runtimeUsername = Value(runtimeSection, "Username") ?? builder.Username;

        if (identity == DatabaseIdentity.Runtime)
        {
            builder.Username = runtimeUsername;
            if (Value(runtimeSection, "Password") is { } runtimePassword)
            {
                builder.Password = runtimePassword;
            }

            if (string.IsNullOrWhiteSpace(builder.Username))
            {
                throw new InvalidOperationException($"Runtime database username is not configured ('{RuntimeSectionName}:Username' or the base connection string).");
            }

            if (Is(builder.Username, DatabaseRoles.Owner) || Is(builder.Username, DatabaseRoles.Maintenance)
                || (Value(migrationSection, "Username") is { } migrationUser && Is(builder.Username, migrationUser)))
            {
                throw new InvalidOperationException(
                    $"Runtime must not connect as the migration/owner or maintenance identity (resolved '{builder.Username}'). " +
                    $"Use '{DatabaseRoles.Runtime}' (ADR-0001).");
            }
        }
        else
        {
            // No fallback: a migration identity must be configured explicitly.
            builder.Username = Value(migrationSection, "Username")
                ?? throw new InvalidOperationException(
                    $"Migration database identity is not configured: set '{MigrationSectionName}:Username' " +
                    $"(e.g. '{DatabaseRoles.Owner}') and its password via environment / user-secrets. " +
                    "Migrations never run with runtime credentials.");

            // Never inherit the runtime password from the base connection string.
            builder.Password = Value(migrationSection, "Password");

            if (Is(builder.Username, DatabaseRoles.Runtime) || (!string.IsNullOrWhiteSpace(runtimeUsername) && Is(builder.Username, runtimeUsername))
                || Is(builder.Username, DatabaseRoles.Maintenance))
            {
                throw new InvalidOperationException(
                    $"Migrations must not run as the runtime or maintenance identity (resolved '{builder.Username}'). " +
                    $"Use '{DatabaseRoles.Owner}' (ADR-0001).");
            }
        }

        // ADR-0001 §2.8: pooled connections must be reset, otherwise RLS session scope can leak to the next user.
        if (builder.NoResetOnClose)
        {
            throw new InvalidOperationException("'No Reset On Close' is forbidden (ADR-0001): it leaks session scope across pooled connections.");
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

    private static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Value(IConfiguration section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
