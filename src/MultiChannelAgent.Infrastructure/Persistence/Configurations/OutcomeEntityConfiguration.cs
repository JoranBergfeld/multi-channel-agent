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
        builder.Property(e => e.Code).HasMaxLength(128).IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(4 * 1024).IsRequired();

        // One Outcome per Turn: the foreign key also enforces that an Outcome can only be recorded
        // for a Turn that was durably accepted.
        builder.HasOne<InboxEntryEntity>()
            .WithOne()
            .HasForeignKey<OutcomeEntity>(e => e.TurnId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
