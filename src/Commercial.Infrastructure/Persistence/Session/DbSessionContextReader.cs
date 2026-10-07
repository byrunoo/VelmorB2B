using System.Data.Common;

namespace Commercial.Infrastructure.Persistence.Session;

/// <summary>
/// INTERNAL read-only view of the PostgreSQL session context (diagnostics, the fingerprint guard's server read-back,
/// integration tests). It can only READ the known <see cref="DbSessionContext.Keys"/>; there is no API to set keys and
/// it is not exposed through any endpoint.
/// </summary>
internal static class DbSessionContextReader
{
    private const string ReadFingerprintSql = "SELECT current_setting('commercial.scope_fingerprint', true)";

    /// <summary>All known keys; a missing setting is reported as <c>null</c>.</summary>
    public static async Task<IReadOnlyDictionary<string, string?>> ReadAllAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + string.Join(", ", DbSessionContext.Keys.Select((_, i) => $"current_setting(@k{i}, true)"));
        for (var i = 0; i < DbSessionContext.Keys.Count; i++)
        {
            AddParameter(command, $"k{i}", DbSessionContext.Keys[i]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("current_setting returned no row.");
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < DbSessionContext.Keys.Count; i++)
        {
            values[DbSessionContext.Keys[i]] = reader.IsDBNull(i) ? null : reader.GetString(i);
        }

        return values;
    }

    /// <summary>The fingerprint PostgreSQL actually holds for this session (empty when unset).</summary>
    public static async Task<string> ReadFingerprintAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ReadFingerprintSql;
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? string.Empty;
    }

    public static string ReadFingerprint(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ReadFingerprintSql;
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    internal static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
