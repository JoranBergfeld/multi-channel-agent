using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class OutcomeEntityConfiguration : IEntityTypeConfiguration<OutcomeEntity>
{
    public void Configure(EntityTypeBuilder<OutcomeEntity> builder)
    {
        builder.ToTable("Outcomes");
        builder.HasKey(e => e.TurnId);

        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Category).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Code).HasMaxLength(128).IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(4 * 1024).IsRequired();
        builder.Property(e => e.Payload).HasMaxLength(32 * 1024);

        // Lets a cleanup pass find exactly the expired payloads without scanning every Outcome ever
        // recorded. Filtered to rows that still hold one, since that is all cleanup ever looks at.
        builder.HasIndex(e => e.PayloadExpiresAtTicks).HasFilter("PayloadExpiresAtTicks IS NOT NULL");

        // One Outcome per Turn: the foreign key also enforces that an Outcome can only be recorded
        // for a Turn that was durably accepted.
        builder.HasOne<InboxEntryEntity>()
            .WithOne()
            .HasForeignKey<OutcomeEntity>(e => e.TurnId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
