using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Infrastructure;
using MultiChannelAgent.Infrastructure.Voice;

namespace MultiChannelAgent.IntegrationTests.Voice;

public sealed class VoiceGatewayDiTests
{
    private static ServiceProvider BuildProvider(bool voiceEnabled)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Entra:UseManagedIdentityForGraph"] = "true",
            })
            .Build();

        services.AddMultiChannelAgentInfrastructure("Server=(localdb);Database=test;", config);
        services.AddSingleton(new VoiceOptions
        {
            Enabled = voiceEnabled,
            Endpoint = voiceEnabled ? "wss://test.services.ai.azure.com/voice" : null,
            Model = voiceEnabled ? "test-model" : null,
        });

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Disabled_voice_resolves_DisabledVoiceLiveGateway()
    {
        using var provider = BuildProvider(voiceEnabled: false);

        var gateway = provider.GetRequiredService<IVoiceLiveGateway>();

        Assert.IsType<DisabledVoiceLiveGateway>(gateway);
    }

    [Fact]
    public void Enabled_voice_resolves_AzureVoiceLiveGateway()
    {
        using var provider = BuildProvider(voiceEnabled: true);

        var gateway = provider.GetRequiredService<IVoiceLiveGateway>();

        Assert.IsType<AzureVoiceLiveGateway>(gateway);
    }

    [Fact]
    public void Gateway_registry_is_singleton()
    {
        using var provider = BuildProvider(voiceEnabled: true);

        var registry1 = provider.GetRequiredService<GatewayRegistry>();
        var registry2 = provider.GetRequiredService<GatewayRegistry>();

        Assert.Same(registry1, registry2);
    }

    [Fact]
    public void Gateway_is_singleton()
    {
        using var provider = BuildProvider(voiceEnabled: false);

        var gateway1 = provider.GetRequiredService<IVoiceLiveGateway>();
        var gateway2 = provider.GetRequiredService<IVoiceLiveGateway>();

        Assert.Same(gateway1, gateway2);
    }
}
