using Commercial.Application.Errors;
using Commercial.Domain.Identity;

namespace Commercial.Application.Abstractions.Scope;

/// <summary>
/// The ONLY request headers that participate in business-scope selection. They are SELECTION HINTS, never trusted
/// authority: every value is checked against the caller's server-side memberships by
/// <see cref="IExecutionScopeResolver"/>. There is deliberately no tenant or customer-account header — tenant,
/// company and customer account are always derived from a validated membership / binding.
/// </summary>
public static class ContextHeaders
{
    /// <summary>WORKFORCE: the company to act in. CUSTOMER: optional consistency check against the active binding.</summary>
    public const string ActiveCompanyId = "X-Active-Company-Id";

    /// <summary>CUSTOMER: opaque id of the caller's own customer binding to act under.</summary>
    public const string ActiveCustomerBindingId = "X-Active-Customer-Binding-Id";
}

/// <summary>Why a business scope could not be (fully) established.</summary>
public enum ActiveContextError
{
    None = 0,
    ActiveCompanyRequired,
    ActiveCompanyForbidden,
    ActiveCustomerBindingRequired,
    ActiveCustomerBindingForbidden,
    ActiveCustomerBindingInvalid,
    ExecutionScopeInvalid,

    /// <summary>Malformed X-Active-Company-Id (reported as BAD_REQUEST).</summary>
    ActiveCompanyMalformed,

    /// <summary>CUSTOMER principal without any active binding (reported as ACTIVE_CUSTOMER_BINDING_FORBIDDEN).</summary>
    CustomerContextUnavailable,
}

/// <summary>Transport-neutral classification; the API maps it to HTTP status codes.</summary>
public enum ActiveContextErrorCategory
{
    None = 0,
    BadRequest,
    Forbidden,
}

public static class ActiveContextErrors
{
    public static string Code(ActiveContextError error) => error switch
    {
        ActiveContextError.ActiveCompanyRequired => ErrorCodes.ActiveCompanyRequired,
        ActiveContextError.ActiveCompanyForbidden => ErrorCodes.ActiveCompanyForbidden,
        ActiveContextError.ActiveCustomerBindingRequired => ErrorCodes.ActiveCustomerBindingRequired,
        ActiveContextError.ActiveCustomerBindingForbidden => ErrorCodes.ActiveCustomerBindingForbidden,
        ActiveContextError.CustomerContextUnavailable => ErrorCodes.ActiveCustomerBindingForbidden,
        ActiveContextError.ActiveCustomerBindingInvalid => ErrorCodes.ActiveCustomerBindingInvalid,
        ActiveContextError.ExecutionScopeInvalid => ErrorCodes.ExecutionScopeInvalid,
        ActiveContextError.ActiveCompanyMalformed => ErrorCodes.BadRequest,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
    };

    public static ActiveContextErrorCategory Category(ActiveContextError error) => error switch
    {
        ActiveContextError.None => ActiveContextErrorCategory.None,
        ActiveContextError.ActiveCompanyRequired or ActiveContextError.ActiveCustomerBindingRequired
            or ActiveContextError.ActiveCustomerBindingInvalid or ActiveContextError.ActiveCompanyMalformed => ActiveContextErrorCategory.BadRequest,
        _ => ActiveContextErrorCategory.Forbidden,
    };

    /// <summary>Client-safe, non-disclosing messages (never echo ids or reveal whether a foreign binding exists).</summary>
    public static string Message(ActiveContextError error) => error switch
    {
        ActiveContextError.ActiveCompanyRequired => $"This operation requires an active company. Select one with the {ContextHeaders.ActiveCompanyId} header.",
        ActiveContextError.ActiveCompanyForbidden => "The selected company is not available to the caller.",
        ActiveContextError.ActiveCustomerBindingRequired => $"The caller has several customer memberships. Select one with the {ContextHeaders.ActiveCustomerBindingId} header.",
        ActiveContextError.ActiveCustomerBindingForbidden => "The selected customer membership is not available to the caller.",
        ActiveContextError.CustomerContextUnavailable => "No active customer membership is available to the caller.",
        ActiveContextError.ActiveCustomerBindingInvalid => $"The {ContextHeaders.ActiveCustomerBindingId} header is malformed.",
        ActiveContextError.ActiveCompanyMalformed => $"The {ContextHeaders.ActiveCompanyId} header is malformed.",
        ActiveContextError.ExecutionScopeInvalid => "A valid execution scope could not be established for the caller.",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
    };
}

/// <summary>
/// PARSED (syntactically valid) selection hints. Parsing is separate from authorization: a well-formed id here
/// means nothing until the resolver matches it against the caller's own memberships.
/// </summary>
public sealed record ActiveContextRequest(CompanyId? ActiveCompanyId, CustomerBindingId? ActiveCustomerBindingId)
{
    public static ActiveContextRequest None { get; } = new(null, null);

    /// <summary>
    /// Parses raw header values. Absent header → null. Repeated header, empty value, non-GUID, or the empty GUID →
    /// <see cref="ActiveContextError.ActiveCustomerBindingInvalid"/> / <see cref="ActiveContextError.ActiveCompanyMalformed"/> (400).
    /// </summary>
    public static (ActiveContextRequest? Request, ActiveContextError Error) Parse(
        IReadOnlyList<string?>? activeCompanyValues,
        IReadOnlyList<string?>? activeCustomerBindingValues)
    {
        if (!TryParseId(activeCustomerBindingValues, out var binding))
        {
            return (null, ActiveContextError.ActiveCustomerBindingInvalid);
        }

        if (!TryParseId(activeCompanyValues, out var company))
        {
            return (null, ActiveContextError.ActiveCompanyMalformed);
        }

        return (new ActiveContextRequest(
            company is { } c ? new CompanyId(c) : null,
            binding is { } b ? new CustomerBindingId(b) : null), ActiveContextError.None);
    }

    private static bool TryParseId(IReadOnlyList<string?>? values, out Guid? id)
    {
        id = null;
        if (values is null || values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0])
            || !Guid.TryParseExact(values[0]!.Trim(), "D", out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        id = parsed;
        return true;
    }
}
