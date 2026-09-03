using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MultiChannelAgent.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so `dotnet ef migrations` can create a <see cref="MultiChannelAgentDbContext"/>
/// without a runnable startup project. The connection string here is never used at runtime; the host
/// always supplies its own configured connection string.
/// </summary>
public sealed class MultiChannelAgentDbContextFactory : IDesignTimeDbContextFactory<MultiChannelAgentDbContext>
{
    public MultiChannelAgentDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MultiChannelAgentDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=localhost;Database=MultiChannelAgent_DesignTime;Trusted_Connection=True;TrustServerCertificate=True;",
            sql => sql.MigrationsAssembly(typeof(MultiChannelAgentDbContext).Assembly.FullName));

        return new MultiChannelAgentDbContext(optionsBuilder.Options);
    }
}
