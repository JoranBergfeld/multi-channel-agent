using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Shared lifecycle for SQL-backed scenarios: positively decides (via <see cref="DockerTestPolicy"/>
/// and <see cref="DockerDaemonProbe"/>) whether to skip, fail, or proceed; then, only when proceeding,
/// stands up an ephemeral SQL Server container, applies production EF Core migrations, and exposes a
/// real <see cref="CustomWebApplicationFactory"/>. Startup failures once the daemon is confirmed
/// reachable are never caught here — they are real bugs and must fail the test.
/// </summary>
public abstract class SqlIntegrationTestBase : IAsyncLifetime
{
    private MsSqlContainer? _sqlContainer;

    protected bool DockerAvailable { get; private set; } = true;

    protected CustomWebApplicationFactory? Factory { get; private set; }

    public async Task InitializeAsync()
    {
        var dockerRequired = DockerTestPolicy.IsDockerRequired(Environment.GetEnvironmentVariable);
        var daemonAvailable = await new DockerDaemonProbe().IsAvailableAsync(CancellationToken.None);

        if (DockerTestPolicy.MaySkip(dockerRequired, daemonAvailable))
        {
            DockerAvailable = false;
            return;
        }

        if (!daemonAvailable)
        {
            // dockerRequired must be true here (otherwise MaySkip above would have returned true),
            // so this is the CI contract: a missing/broken Docker daemon must fail loudly, never
            // silently skip.
            throw new InvalidOperationException(
                $"{DockerTestPolicy.RequireDockerTestsVariableName}=true but the Docker daemon is not " +
                "reachable; this SQL-backed scenario cannot silently skip.");
        }

        // The daemon is confirmed reachable, so any failure below (bad image, bad configuration,
        // migration failure) is a real bug and must propagate as a failing test.
        _sqlContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
        await _sqlContainer.StartAsync();

        Factory = new CustomWebApplicationFactory(_sqlContainer.GetConnectionString());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_sqlContainer is not null)
        {
            await _sqlContainer.DisposeAsync();
        }
    }
}
