namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Pure, Docker-free coverage of the skip/fail policy for Docker-backed SQL scenarios: CI sets
/// `REQUIRE_DOCKER_TESTS=true`, which removes the ability to skip on a startup failure, so a broken
/// Docker/container setup in CI is always a real failure rather than a silently-passing skip. Locally
/// (the variable unset or not "true"), skipping is allowed only once the daemon has been positively
/// probed and found unreachable.
/// </summary>
public class DockerTestPolicyTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void Docker_is_required_when_the_variable_is_true_case_insensitively(string value)
    {
        Assert.True(DockerTestPolicy.IsDockerRequired(_ => value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    public void Docker_is_not_required_for_any_value_other_than_true(string? value)
    {
        Assert.False(DockerTestPolicy.IsDockerRequired(_ => value));
    }

    [Fact]
    public void A_missing_daemon_may_skip_only_when_docker_is_not_required()
    {
        Assert.True(DockerTestPolicy.MaySkip(dockerRequired: false, daemonAvailable: false));
    }

    [Fact]
    public void A_missing_daemon_may_never_skip_when_docker_is_required()
    {
        Assert.False(DockerTestPolicy.MaySkip(dockerRequired: true, daemonAvailable: false));
    }

    [Fact]
    public void An_available_daemon_never_needs_to_skip_regardless_of_the_requirement()
    {
        Assert.False(DockerTestPolicy.MaySkip(dockerRequired: false, daemonAvailable: true));
        Assert.False(DockerTestPolicy.MaySkip(dockerRequired: true, daemonAvailable: true));
    }
}
