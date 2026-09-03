using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ParticipantEntityConfiguration : IEntityTypeConfiguration<ParticipantEntity>
{
    public void Configure(EntityTypeBuilder<ParticipantEntity> builder)
    {
        builder.ToTable("Participants");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.DisplayName).HasMaxLength(256).IsRequired();

        // Explicit true default: every already-existing Participant row (created before this
        // column existed) must be treated as active, matching the invariant that only an explicit
        // recovery-flow directory revalidation ever marks a Participant inactive. Leaving this to
        // the CLR default would instead default every pre-existing row to false.
        builder.Property(e => e.IsActive).HasDefaultValue(true);

        // Supports the recovery flow's orphan-listing query: current Owner Memberships joined to
        // Participants whose IsActive is false.
        builder.HasIndex(e => e.IsActive);
    }
}
