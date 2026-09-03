using Docker.DotNet;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Positively determines whether the Docker daemon is reachable, rather than inferring "Docker is
/// unavailable" from whatever exception a later container build/start might throw. A successful ping
/// is the only signal this probe trusts as "absent"; any other failure while bringing up a container
/// (bad image, bad configuration, a broken Testcontainers setup) must still surface as a real test
/// failure, never a skip.
/// </summary>
public interface IDockerDaemonProbe
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}

public sealed class DockerDaemonProbe : IDockerDaemonProbe
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new DockerClientBuilder().Build();
            await client.System.PingAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
