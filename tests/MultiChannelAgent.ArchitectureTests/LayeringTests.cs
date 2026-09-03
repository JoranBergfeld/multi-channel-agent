using NetArchTest.Rules;

namespace MultiChannelAgent.ArchitectureTests;

/// <summary>
/// Enforces the layered dependency direction agreed for this foundation: Domain has no outward
/// dependencies; Application depends only on Domain; Infrastructure depends on Domain/Application;
/// Host may depend on all three. A violation here is a compile-time-detectable architecture drift.
/// </summary>
public class LayeringTests
{
    private const string DomainNamespace = "MultiChannelAgent.Domain";
    private const string ApplicationNamespace = "MultiChannelAgent.Application";
    private const string InfrastructureNamespace = "MultiChannelAgent.Infrastructure";
    private const string HostNamespace = "MultiChannelAgent.Host";

    [Fact]
    public void Domain_does_not_depend_on_application_infrastructure_or_host()
    {
        var result = Types.InAssembly(typeof(Domain.Turns.InboundTurn).Assembly)
            .That().ResideInNamespace(DomainNamespace)
            .ShouldNot().HaveDependencyOnAny(ApplicationNamespace, InfrastructureNamespace, HostNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_host()
    {
        var result = Types.InAssembly(typeof(Application.Turns.TurnAcceptanceService).Assembly)
            .That().ResideInNamespace(ApplicationNamespace)
            .ShouldNot().HaveDependencyOnAny(InfrastructureNamespace, HostNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_host()
    {
        var result = Types.InAssembly(typeof(Infrastructure.Persistence.MultiChannelAgentDbContext).Assembly)
            .That().ResideInNamespace(InfrastructureNamespace)
            .ShouldNot().HaveDependencyOnAny(HostNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule failed."
            : "Violating types: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
