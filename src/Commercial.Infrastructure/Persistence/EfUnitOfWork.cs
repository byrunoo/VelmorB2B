using Commercial.Application.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Commercial.Infrastructure.Persistence;

internal sealed class EfUnitOfWork(CommercialDbContext db) : IUnitOfWork
{
    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (db.Database.CurrentTransaction is not null)
        {
            // Nested use joins the ambient transaction; the outermost call commits.
            var nested = await work(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return nested;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            (Db: db, Work: work),
            static async (_, state, ct) =>
            {
                await using var transaction = await state.Db.Database.BeginTransactionAsync(ct);
                var result = await state.Work(ct);
                await state.Db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            },
            verifySucceeded: null,
            cancellationToken);
    }
}
