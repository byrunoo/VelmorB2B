namespace Commercial.Application.Errors;

/// <summary>
/// Base for expected application failures that map to the standard error envelope.
/// Messages of these exceptions are client-safe; messages of any other exception are not.
/// </summary>
public abstract class CommercialException : Exception
{
    protected CommercialException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class ErrorCodes
{
    public const string Validation = "VALIDATION_ERROR";
    public const string Conflict = "CONFLICT";
    public const string NotFound = "NOT_FOUND";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string BadRequest = "BAD_REQUEST";
    public const string Internal = "INTERNAL_ERROR";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";

    // Active business-scope selection (Phase 1 WS2, docs/architecture/execution-scope.md).
    public const string ActiveCompanyRequired = "ACTIVE_COMPANY_REQUIRED";
    public const string ActiveCompanyForbidden = "ACTIVE_COMPANY_FORBIDDEN";
    public const string ActiveCustomerBindingRequired = "ACTIVE_CUSTOMER_BINDING_REQUIRED";
    public const string ActiveCustomerBindingForbidden = "ACTIVE_CUSTOMER_BINDING_FORBIDDEN";
    public const string ActiveCustomerBindingInvalid = "ACTIVE_CUSTOMER_BINDING_INVALID";
    public const string ExecutionScopeInvalid = "EXECUTION_SCOPE_INVALID";
}

public sealed class ValidationFailedException : CommercialException
{
    public ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
        : base(ErrorCodes.Validation, "One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

public sealed class ConflictException(string message, string code = ErrorCodes.Conflict)
    : CommercialException(code, message);

public sealed class NotFoundException(string message)
    : CommercialException(ErrorCodes.NotFound, message);

public sealed class ForbiddenAccessException(string message)
    : CommercialException(ErrorCodes.Forbidden, message);
