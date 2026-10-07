using System.Net;
using System.Text.Json;
using Json.Schema;

namespace Commercial.ContractTests;

/// <summary>
/// Phase 0 contract test #2 — every error response conforms to the standard envelope
/// (Schemas/error-envelope.schema.json) with correct status/code semantics, a correlation ID and no
/// leaked internals. Commercial Platform's OWN contract only — no external provider contracts.
/// </summary>
public sealed class ErrorEnvelopeContractTests : IClassFixture<ContractApiFactoryWithProbes>
{
    internal static readonly JsonSchema Schema = JsonSchema.FromFile(
        Path.Combine(OpenApiSnapshotTests.RepositoryRoot(), "tests/Commercial.ContractTests/Schemas/error-envelope.schema.json"));

    private readonly ContractApiFactoryWithProbes _factory;

    public ErrorEnvelopeContractTests(ContractApiFactoryWithProbes factory) => _factory = factory;

    public static TheoryData<string, string, string?, HttpStatusCode, string> Cases => new()
    {
        // method, path, dev principal header, expected status, expected code
        { "POST", "/api/v1/diagnostics/outbox-pings", null, HttpStatusCode.BadRequest, "VALIDATION_ERROR" },
        { "GET", "/__contract/conflict", null, HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT" },
        { "GET", "/__contract/not-found", null, HttpStatusCode.NotFound, "NOT_FOUND" },
        { "GET", "/api/v1/does-not-exist", null, HttpStatusCode.NotFound, "NOT_FOUND" },
        { "GET", "/api/v1/system/me", null, HttpStatusCode.Unauthorized, "UNAUTHORIZED" },
        { "GET", "/api/v1/system/access-probe/workforce", "CUSTOMER", HttpStatusCode.Forbidden, "FORBIDDEN" },
        { "GET", "/__contract/unhandled", null, HttpStatusCode.InternalServerError, "INTERNAL_ERROR" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Error_responses_conform_to_the_envelope(string method, string path, string? principal, HttpStatusCode status, string code)
    {
        var (response, body, json) = await SendAsync(method, path, principal, correlationId: "contract-" + (int)status);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var evaluation = Schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, $"Envelope does not match schema: {body}");

        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal("contract-" + (int)status, json.GetProperty("correlationId").GetString());
        Assert.Equal(json.GetProperty("correlationId").GetString(), response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Validation_errors_are_field_keyed_and_distinct_from_conflicts()
    {
        var (_, _, validation) = await SendAsync("POST", "/api/v1/diagnostics/outbox-pings", null, "v-1", new string('x', 201));
        var (_, _, conflict) = await SendAsync("GET", "/__contract/conflict", null, "c-1");

        Assert.Equal(400, validation.GetProperty("status").GetInt32());
        Assert.True(validation.GetProperty("errors").TryGetProperty("note", out var noteErrors));
        Assert.True(noteErrors.GetArrayLength() > 0);

        Assert.Equal(409, conflict.GetProperty("status").GetInt32());
        Assert.Empty(conflict.GetProperty("errors").EnumerateObject());
        Assert.NotEqual(validation.GetProperty("type").GetString(), conflict.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Unauthenticated_and_forbidden_are_distinct()
    {
        var (anonymous, _, a) = await SendAsync("GET", "/api/v1/system/access-probe/workforce", null, "auth-1");
        var (customer, _, c) = await SendAsync("GET", "/api/v1/system/access-probe/workforce", "CUSTOMER", "auth-2");
        var (workforce, _, _) = await SendAsync("GET", "/api/v1/system/access-probe/workforce", "WORKFORCE", "auth-3");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("UNAUTHORIZED", a.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, customer.StatusCode);
        Assert.Equal("FORBIDDEN", c.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, workforce.StatusCode);
    }

    [Fact]
    public async Task Unhandled_errors_never_leak_messages_or_stack_traces()
    {
        var (_, body, json) = await SendAsync("GET", "/__contract/unhandled", null, "leak-1");

        Assert.DoesNotContain("db-secret-host", body, StringComparison.Ordinal);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
        Assert.Equal("INTERNAL_ERROR", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Version_endpoint_contract()
    {
        var (response, _, json) = await SendAsync("GET", "/api/v1/system/version", null, "ver-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Commercial.Api", json.GetProperty("service").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("environment").GetString()));
    }

    [Fact]
    public async Task Liveness_contract_does_not_require_dependencies()
    {
        var (response, _, json) = await SendAsync("GET", "/health/live", null, "live-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", json.GetProperty("status").GetString());
    }

    private async Task<(HttpResponseMessage Response, string Body, JsonElement Json)> SendAsync(
        string method, string path, string? principal, string correlationId, string? note = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Correlation-Id", correlationId);
        if (principal is not null)
        {
            request.Headers.Add("X-Dev-Principal-Type", principal);
        }

        if (method == "POST")
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { note = note ?? new string('x', 500) }),
                System.Text.Encoding.UTF8,
                "application/json");
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, body, JsonDocument.Parse(body).RootElement.Clone());
    }
}
