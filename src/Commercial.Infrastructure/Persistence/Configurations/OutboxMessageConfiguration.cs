using Commercial.Infrastructure.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Commercial.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(m => m.CausationId).HasMaxLength(128);
        builder.Property(m => m.LastError).HasMaxLength(4000);
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(m => m.LockedBy).HasMaxLength(200);

        // Claim query support: unprocessed rows ordered by due time.
        builder.HasIndex(m => new { m.NextAttemptAtUtc, m.OccurredAtUtc })
            .HasDatabaseName("ix_outbox_messages_claimable")
            .HasFilter("processed_at_utc IS NULL");

        builder.HasIndex(m => m.CorrelationId).HasDatabaseName("ix_outbox_messages_correlation_id");
    }
}
