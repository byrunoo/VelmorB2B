using System.Security.Cryptography;
using System.Text;

namespace Commercial.Infrastructure.Hosting;

/// <summary>
/// Computes a PostgreSQL SCRAM-SHA-256 password verifier (RFC 5802 / RFC 7677, PostgreSQL storage format) on the
/// client, so role passwords can be set with <c>ALTER ROLE … PASSWORD '&lt;verifier&gt;'</c> without the plain-text
/// password ever reaching the server or its logs. Used by DBA tooling only (scripts/shared-db.ps1).
/// </summary>
public static class ScramSha256
{
    public const int DefaultIterations = 4096;

    public static string CreateVerifier(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        return CreateVerifier(password, salt, iterations);
    }

    internal static string CreateVerifier(string password, byte[] salt, int iterations)
    {
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}");
    }

    /// <summary>A safe SQL statement setting the verifier (verifier is base64/ASCII only; role name is a fixed identifier).</summary>
    public static string AlterRolePasswordSql(string role, string verifier)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(role, "^[a-z_][a-z0-9_]*$"))
        {
            throw new ArgumentException("Unexpected role name.", nameof(role));
        }

        if (!verifier.StartsWith("SCRAM-SHA-256$", StringComparison.Ordinal) || verifier.Contains('\'', StringComparison.Ordinal))
        {
            throw new ArgumentException("Not a SCRAM verifier.", nameof(verifier));
        }

        return $"ALTER ROLE {role} PASSWORD '{verifier}'";
    }
}
