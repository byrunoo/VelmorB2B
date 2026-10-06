namespace Commercial.Application.Abstractions.Requests;

// Minimal use-case pipeline (no third-party mediator). Commands mutate state; queries do not.

#pragma warning disable CA1040 // Marker interfaces carry the result type for dispatch.
public interface ICommand<TResult>
{
}

public interface IQuery<TResult>
{
}
#pragma warning restore CA1040

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Validation pipeline step. Validators return field-keyed messages; the dispatcher aggregates
/// them and fails with <see cref="Errors.ValidationFailedException"/> before the handler runs.
/// </summary>
public interface IRequestValidator<in TRequest>
{
    IEnumerable<ValidationFailure> Validate(TRequest request);
}

public sealed record ValidationFailure(string Field, string Message);

public interface IRequestDispatcher
{
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken);

    Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken);
}
