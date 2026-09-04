using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Boots the real Host against an in-memory SQLite database instead of a Testcontainers SQL Server
/// instance, mirroring the Docker-free pattern already proven at the repository seam by
/// <see cref="SqlInboxStoreConcurrencyTests"/>: fast, real relational-engine coverage (no Docker
/// required) for HTTP-boundary scenarios that never need SQL Server-specific behavior. The periodic
/// hosted workers are removed for the same reason <see cref="CustomWebApplicationFactory"/> removes
/// them - tests here only need the endpoint's own request handling, not the background workers.
/// </summary>
public sealed class SqliteWebApplicationFactory : WebApplicationFactory<Program>
{
    // A shared-cache in-memory SQLite database only persists while at least one connection to it
    // remains open, so this connection is kept open for the factory's whole lifetime purely to keep
    // the database alive across the separate DbContext instances each request scope resolves.
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
    private readonly TimeProvider? _timeProvider;
    private readonly Action<IServiceCollection>? _configureTestServices;

    /// <summary>
    /// <paramref name="timeProvider"/> lets a scenario advance time deliberately - the ten-minute
    /// confirmation lifetime is behavior, and a test that proved it by sleeping would be both slow
    /// and flaky.
    ///
    /// <paramref name="configureTestServices"/> lets one scenario add what the test server itself is
    /// missing rather than what the Host is - see <see cref="ServerRequestSizeLimitExtensions"/>, which
    /// supplies the request-body-size feature <c>Microsoft.AspNetCore.TestHost</c> does not implement.
    /// </summary>
    public SqliteWebApplicationFactory(
        TimeProvider? timeProvider = null,
        Action<IServiceCollection>? configureTestServices = null)
    {
        _timeProvider = timeProvider;
        _configureTestServices = configureTestServices;
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Program.cs requires a non-null connection string at startup; the real connection
                // used by MultiChannelAgentDbContext is swapped to SQLite below instead.
                ["ConnectionStrings:MultiChannelAgent"] = "unused-placeholder",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();

            // Multiple EF Core database provider services (SQL Server from the production DI
            // registration this factory boots on top of, plus SQLite added below) cannot coexist for
            // the same DbContext type - EF Core throws at startup if both remain registered. Removing
            // every EF Core-owned descriptor first (not just DbContextOptions<T>) clears all of the
            // provider-specific registrations the original SQL Server registration added, leaving a
            // clean slate for the SQLite registration that follows.
            var efCoreDescriptors = services
                .Where(d => d.ServiceType.Namespace is { } ns && ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
                .ToList();
            foreach (var descriptor in efCoreDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<MultiChannelAgentDbContext>(options => options.UseSqlite(_connectionString));

            // Registered last so it wins over the infrastructure's TimeProvider.System.
            if (_timeProvider is not null)
            {
                services.AddSingleton(_timeProvider);
            }

            _configureTestServices?.Invoke(services);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>().Database.EnsureCreated();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAliveConnection.Dispose();
        }
    }
}
