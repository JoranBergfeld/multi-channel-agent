using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class AuthTicketEntityConfiguration : IEntityTypeConfiguration<AuthTicketEntity>
{
    public void Configure(EntityTypeBuilder<AuthTicketEntity> builder)
    {
        builder.ToTable("AuthTickets");
        builder.HasKey(e => e.Key);

        builder.Property(e => e.Key).HasMaxLength(64);
        builder.Property(e => e.ProtectedTicket).IsRequired();

        // Supports a future expiry-sweeping background job; not exercised by this ticket, but the
        // access pattern (find rows past their expiry) is common enough to index up front.
        builder.HasIndex(e => e.ExpiresAtUtc);
    }
}
