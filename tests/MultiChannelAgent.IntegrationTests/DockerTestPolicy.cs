namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The environment contract governing whether a Docker-backed SQL integration scenario may skip when
/// its ephemeral container cannot be brought up. CI sets `REQUIRE_DOCKER_TESTS=true`, which removes
/// the ability to skip entirely: a broken Docker/container setup there must fail the build, never
/// silently pass as a skip. Locally, skipping is allowed only once the Docker daemon has been
/// positively probed and found unreachable (see <see cref="DockerDaemonProbe"/>) — never inferred from
/// whatever exception a later container build/start throws, which could otherwise mask a real bug
/// (bad image, bad configuration) as a clean skip.
/// </summary>
public static class DockerTestPolicy
{
    public const string RequireDockerTestsVariableName = "REQUIRE_DOCKER_TESTS";

    public static bool IsDockerRequired(Func<string, string?> getEnvironmentVariable) =>
        string.Equals(getEnvironmentVariable(RequireDockerTestsVariableName), "true", StringComparison.OrdinalIgnoreCase);

    public static bool MaySkip(bool dockerRequired, bool daemonAvailable) => !dockerRequired && !daemonAvailable;
}
