using Azure.Identity;
using Microsoft.Extensions.Configuration;
using MultiChannelAgent.Infrastructure.Inventories;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Unit coverage for <see cref="GraphCredentialFactory"/>'s fail-fast configuration validation: the
/// factory itself never performs network I/O (constructing a credential is synchronous and local), so
/// these assertions run instantly and prove the "explicit configuration, fail fast" contract without
/// ever contacting Microsoft Graph or Entra.
/// </summary>
public sealed class GraphCredentialFactoryTests
{
    private static IConfiguration ConfigurationFrom(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Missing_tenant_client_and_secret_fails_fast_instead_of_deferring_to_an_unusable_adapter()
    {
        var configuration = ConfigurationFrom(new Dictionary<string, string?>());

        var exception = Assert.Throws<InvalidOperationException>(() => GraphCredentialFactory.Create(configuration));
        Assert.Contains("Authentication:Entra:TenantId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_configuration_missing_only_the_secret_still_fails_fast()
    {
        var configuration = ConfigurationFrom(new Dictionary<string, string?>
        {
            ["Authentication:Entra:TenantId"] = Guid.NewGuid().ToString(),
            ["Authentication:Entra:ClientId"] = Guid.NewGuid().ToString(),
        });

        Assert.Throws<InvalidOperationException>(() => GraphCredentialFactory.Create(configuration));
    }

    [Fact]
    public void Complete_tenant_client_secret_configuration_builds_a_client_secret_credential()
    {
        var configuration = ConfigurationFrom(new Dictionary<string, string?>
        {
            ["Authentication:Entra:TenantId"] = Guid.NewGuid().ToString(),
            ["Authentication:Entra:ClientId"] = Guid.NewGuid().ToString(),
            ["Authentication:Entra:ClientSecret"] = "placeholder-secret",
        });

        var credential = GraphCredentialFactory.Create(configuration);

        Assert.IsType<ClientSecretCredential>(credential);
    }

    [Fact]
    public void Managed_identity_flag_builds_a_credential_without_requiring_a_client_secret()
    {
        var configuration = ConfigurationFrom(new Dictionary<string, string?>
        {
            ["Authentication:Entra:UseManagedIdentityForGraph"] = "true",
        });

        var credential = GraphCredentialFactory.Create(configuration);

        Assert.IsType<DefaultAzureCredential>(credential);
    }
}
