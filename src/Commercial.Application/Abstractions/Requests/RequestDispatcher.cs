using System.Collections.Concurrent;
using System.Reflection;
using Commercial.Application.Errors;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.Application.Abstractions.Requests;

internal sealed class RequestDispatcher(IServiceProvider services) : IRequestDispatcher
{
    private static readonly ConcurrentDictionary<(Type Request, Type Result, bool IsCommand), MethodInfo> Invokers = new();

    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Invoke<TResult>(command, isCommand: true, cancellationToken);
    }

    public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Invoke<TResult>(query, isCommand: false, cancellationToken);
    }

    private Task<TResult> Invoke<TResult>(object request, bool isCommand, CancellationToken cancellationToken)
    {
        var method = Invokers.GetOrAdd((request.GetType(), typeof(TResult), isCommand), static key =>
            typeof(RequestDispatcher)
                .GetMethod(key.IsCommand ? nameof(HandleCommand) : nameof(HandleQuery), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(key.Request, key.Result));

        return (Task<TResult>)method.Invoke(this, [request, cancellationToken])!;
    }

    private async Task<TResult> HandleCommand<TCommand, TResult>(TCommand command, CancellationToken cancellationToken)
        where TCommand : ICommand<TResult>
    {
        Validate(command);
        return await services.GetRequiredService<ICommandHandler<TCommand, TResult>>().HandleAsync(command, cancellationToken);
    }

    private async Task<TResult> HandleQuery<TQuery, TResult>(TQuery query, CancellationToken cancellationToken)
        where TQuery : IQuery<TResult>
    {
        Validate(query);
        return await services.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, cancellationToken);
    }

    private void Validate<TRequest>(TRequest request)
    {
        var failures = services.GetServices<IRequestValidator<TRequest>>()
            .SelectMany(v => v.Validate(request))
            .ToList();

        if (failures.Count > 0)
        {
            throw new ValidationFailedException(failures
                .GroupBy(f => f.Field, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(f => f.Message).ToArray(), StringComparer.Ordinal));
        }
    }
}
