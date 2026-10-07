using Commercial.Application;
using Commercial.Application.Abstractions.Requests;
using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Diagnostics;
using Commercial.Application.Errors;
using Commercial.Connectors.Abstractions;
using Commercial.Domain.Primitives;
using Commercial.Infrastructure.Hosting;
using Commercial.Infrastructure.Messaging.Outbox;
using Commercial.Infrastructure.Persistence.Conventions;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.UnitTests;

public sealed class DomainPrimitiveTests
{
    [Fact]
    public void Result_success_and_failure_are_consistent()
    {
        var ok = Result.Success(42);
        var failed = Result.Failure<int>(new DomainError("SOMETHING_FAILED", "nope"));

        Assert.True(ok.IsSuccess);
        Assert.Equal(42, ok.Value);
        Assert.True(failed.IsFailure);
        Assert.Equal("SOMETHING_FAILED", failed.Error.Code);
        Assert.Throws<InvalidOperationException>(() => failed.Value);
    }

    [Fact]
    public void Error_requires_a_code() =>
        Assert.ThrowsAny<ArgumentException>(() => new DomainError(" ", "message"));
}

public sealed class ExecutionScopeTests
{
    [Fact]
    public void Accessor_returns_unscoped_by_default_and_restores_previous_scope()
    {
        var accessor = new AmbientExecutionScopeAccessor();
        Assert.Same(ExecutionScope.Unscoped, accessor.Current);

        using (accessor.Begin(ExecutionScope.Anonymous("outer")))
        {
            using (accessor.Begin(ExecutionScope.SystemInfrastructure("inner")))
            {
                Assert.Equal("inner", accessor.Current.CorrelationId);
                Assert.Equal(PrincipalType.System, accessor.Current.PrincipalType);
            }

            Assert.Equal("outer", accessor.Current.CorrelationId);
        }

        Assert.Same(ExecutionScope.Unscoped, accessor.Current);
    }

    [Fact]
    public async Task Scopes_do_not_leak_between_concurrent_async_flows()
    {
        var accessor = new AmbientExecutionScopeAccessor();
        var observed = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            using var _ = accessor.Begin(ExecutionScope.Anonymous($"flow-{i}"));
            await Task.Delay(Random.Shared.Next(1, 10));
            return (Expected: $"flow-{i}", Actual: accessor.Current.CorrelationId);
        }));

        Assert.All(observed, o => Assert.Equal(o.Expected, o.Actual));
    }

    [Theory]
    [InlineData("WORKFORCE", PrincipalType.Workforce)]
    [InlineData("CUSTOMER", PrincipalType.Customer)]
    [InlineData("workforce", PrincipalType.Anonymous)]
    [InlineData(null, PrincipalType.Anonymous)]
    public void Principal_type_claim_contract_is_exact(string? wire, PrincipalType expected) =>
        Assert.Equal(expected, PrincipalTypeValues.Parse(wire));
}

public sealed class RequestPipelineTests
{
    [Fact]
    public async Task Validation_failures_are_aggregated_before_the_handler_runs()
    {
        await using var provider = new ServiceCollection().AddLogging().AddCommercialApplication().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IRequestDispatcher>();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            dispatcher.SendAsync(new EnqueueDiagnosticPing(new string('x', DiagnosticPingMessage.MaxNoteLength + 1)), CancellationToken.None));

        Assert.Equal(ErrorCodes.Validation, ex.Code);
        Assert.True(ex.Errors.ContainsKey("note"));
    }

    [Fact]
    public async Task Queries_are_dispatched_to_their_handler()
    {
        var id = Guid.NewGuid();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddCommercialApplication()
            .AddScoped<IDiagnosticPingReadModel>(_ => new FakeReadModel(id))
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var status = await scope.ServiceProvider.GetRequiredService<IRequestDispatcher>()
            .QueryAsync(new GetDiagnosticPingStatus(id), CancellationToken.None);

        Assert.Equal(id, status!.MessageId);
    }

    private sealed class FakeReadModel(Guid id) : IDiagnosticPingReadModel
    {
        public Task<DiagnosticPingStatus?> GetAsync(Guid messageId, CancellationToken cancellationToken) =>
            Task.FromResult<DiagnosticPingStatus?>(messageId == id
                ? new DiagnosticPingStatus(id, "Processed", 1, DateTimeOffset.UnixEpoch, true, "c")
                : null);
    }
}

public sealed class InfrastructurePolicyTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(30, 900)] // capped at MaxRetryDelay (15 min)
    public void Outbox_retry_backoff_is_exponential_and_capped(int attempt, int expectedSeconds)
    {
        var options = new OutboxOptions();
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.RetryDelayFor(attempt));
    }

    [Fact]
    public void Non_utc_datetime_cannot_be_persisted()
    {
        var converter = new UtcDateTimeConverter();
        var toProvider = converter.ConvertToProviderExpression.Compile();

        Assert.Throws<InvalidOperationException>(() => toProvider(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));
        Assert.Throws<InvalidOperationException>(() => toProvider(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)));
        var utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc, toProvider(utc));
        Assert.Equal(DateTimeKind.Utc, converter.ConvertFromProviderExpression.Compile()(new DateTime(2026, 1, 1)).Kind);
    }

    [Theory]
    [InlineData("abc-123_X.y:z", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("<script>", false)]
    public void Correlation_ids_from_clients_are_validated(string value, bool valid) =>
        Assert.Equal(valid, CorrelationId.IsValid(value));

    [Fact]
    public void Correlation_id_length_is_bounded() =>
        Assert.False(CorrelationId.IsValid(new string('a', CorrelationId.MaxLength + 1)));

    [Theory]
    [InlineData("catalog.product.read", true)]
    [InlineData("account.balance.read", true)]
    [InlineData("Catalog.Product", false)]
    [InlineData("single", false)]
    public void Connector_capability_identifiers_are_provider_neutral_dotted_names(string value, bool valid) =>
        Assert.Equal(valid, ConnectorCapability.IsValid(value));
}
