using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Commercial.Infrastructure.Persistence.Conventions;

/// <summary>Rejects non-UTC DateTime writes and marks reads as UTC.</summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => EnsureUtc(v),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
{
    internal static DateTime EnsureUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : throw new InvalidOperationException(
            $"Only UTC DateTime values may be persisted (got Kind={value.Kind}). Use DateTimeOffset/TimeProvider.GetUtcNow().");
}

public sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue ? UtcDateTimeConverter.EnsureUtc(v.Value) : v,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

public static class ConcurrencyConventions
{
    /// <summary>
    /// Optimistic concurrency foundation: maps PostgreSQL's system column <c>xmin</c> as the row
    /// version (no extra column / migration needed). Apply to aggregates as they are introduced.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasXminConcurrencyToken<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Property<uint>("RowVersion").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        return builder;
    }
}
