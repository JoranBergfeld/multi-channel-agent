using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Boots the real Host against a caller-supplied SQL Server connection string (the ephemeral
/// Testcontainers instance). The periodic hosted workers are removed so tests drive processing
/// deterministically through the same internal one-shot operations the workers use, instead of
/// timing a background loop.
/// </summary>
public sealed class CustomWebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MultiChannelAgent"] = connectionString,
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<DbContextOptions<MultiChannelAgentDbContext>>();
            services.AddDbContext<MultiChannelAgentDbContext>(options => options.UseSqlServer(connectionString));
        });
    }
}
