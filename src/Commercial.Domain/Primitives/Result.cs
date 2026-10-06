namespace Commercial.Domain.Primitives;

/// <summary>Explicit success/failure result for expected (non-exceptional) outcomes.</summary>
public class Result
{
    protected Result(bool isSuccess, DomainError error)
    {
        if (isSuccess && error != DomainError.None)
        {
            throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        }

        if (!isSuccess && error == DomainError.None)
        {
            throw new ArgumentException("A failed result must carry an error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public DomainError Error { get; }

    public static Result Success() => new(true, DomainError.None);

    public static Result Failure(DomainError error) => new(false, error);

    public static Result<T> Success<T>(T value) => new(value, true, DomainError.None);

    public static Result<T> Failure<T>(DomainError error) => new(default, false, error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T? value, bool isSuccess, DomainError error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read the value of a failed result.");
}
