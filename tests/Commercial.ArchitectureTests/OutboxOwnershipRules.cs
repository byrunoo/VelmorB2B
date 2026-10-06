using System.Text.RegularExpressions;
using Commercial.ArchitectureTests.Support;
using Commercial.Infrastructure.Messaging.Outbox;

namespace Commercial.ArchitectureTests;

/// <summary>
/// A013 — the outbox claim model must be concurrency-safe and lease based. Semantics are PROVEN by
/// integration tests (Testcontainers); this rule asserts the protocol shape and that the required
/// integration proofs exist, so neither can silently disappear.
/// </summary>
public sealed partial class OutboxOwnershipRules
{
    private const string Failure = "OUTBOX_OWNERSHIP_OR_IDEMPOTENCY_VIOLATION";

    [Fact]
    public void A013_Outbox_row_has_required_ownership_and_retry_fields()
    {
        var required = new Dictionary<string, Type>
        {
            ["Id"] = typeof(Guid),
            ["OccurredAtUtc"] = typeof(DateTimeOffset),
            ["Type"] = typeof(string),
            ["Payload"] = typeof(string),
            ["CorrelationId"] = typeof(string),
            ["CausationId"] = typeof(string),
            ["TenantId"] = typeof(Guid?),
            ["Attempts"] = typeof(int),
            ["NextAttemptAtUtc"] = typeof(DateTimeOffset),
            ["ProcessedAtUtc"] = typeof(DateTimeOffset?),
            ["LastError"] = typeof(string),
            ["Status"] = typeof(OutboxStatus),
            ["LockedBy"] = typeof(string),
            ["LockedUntilUtc"] = typeof(DateTimeOffset?),
        };

        var missing = required
            .Where(kv => typeof(OutboxMessage).GetProperty(kv.Key)?.PropertyType != kv.Value)
            .Select(kv => $"{kv.Key}:{kv.Value.Name}")
            .ToArray();

        Assert.True(missing.Length == 0, $"{Failure}: missing/mistyped outbox fields {string.Join(", ", missing)}");
    }

    [Fact]
    public void A013_Claim_uses_skip_locked_and_persisted_lease()
    {
        var claim = Normalize(OutboxSql.Claim);
        Assert.True(claim.Contains("for update skip locked", StringComparison.Ordinal), $"{Failure}: claim must use FOR UPDATE SKIP LOCKED");
        Assert.True(claim.Contains("locked_by = @lease", StringComparison.Ordinal), $"{Failure}: claim must stamp locked_by");
        Assert.True(claim.Contains("locked_until_utc = @locked_until", StringComparison.Ordinal), $"{Failure}: claim must stamp lease expiry");
        Assert.True(claim.Contains("locked_until_utc <= @now", StringComparison.Ordinal), $"{Failure}: expired leases must be reclaimable");
    }

    [Fact]
    public void A013_Completion_and_failure_are_guarded_by_lease_ownership()
    {
        foreach (var (name, sql) in new[] { ("MarkProcessed", OutboxSql.MarkProcessed), ("MarkFailed", OutboxSql.MarkFailed) })
        {
            Assert.True(
                Normalize(sql).Contains("and locked_by = @lease", StringComparison.Ordinal),
                $"{Failure}: {name} must only update rows still owned by the caller's lease");
        }
    }

    [Theory]
    [InlineData("multi-worker-claim")]
    [InlineData("lease-expiry-recovery")]
    [InlineData("duplicate-delivery-idempotency")]
    [InlineData("hangfire-dispatch")]
    public void A013_Required_integration_proofs_exist(string proof)
    {
        var tagged = Repository.TextFiles("tests/Commercial.IntegrationTests", ".cs")
            .Any(f => File.ReadAllText(f).Contains($"[Trait(\"A013\", \"{proof}\")]", StringComparison.Ordinal));
        Assert.True(tagged, $"{Failure}: no integration test tagged [Trait(\"A013\", \"{proof}\")]");
    }

    private static string Normalize(string sql) => Whitespace().Replace(sql, " ").ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
