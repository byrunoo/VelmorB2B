namespace Commercial.Application.Abstractions.Persistence;

/// <summary>
/// Transaction abstraction. Everything registered with the unit of work inside
/// <paramref name="work"/> (including outbox messages) commits atomically or not at all.
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}
