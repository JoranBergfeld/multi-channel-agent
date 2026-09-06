using MultiChannelAgent.Application.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

/// <summary>
/// Runs the <see cref="VoiceSessionStoreContractTests"/> against <see cref="InMemoryVoiceSessionStore"/>.
/// </summary>
public sealed class InMemoryVoiceSessionStoreTests : VoiceSessionStoreContractTests
{
    protected override IVoiceSessionStore CreateStore() => new InMemoryVoiceSessionStore();
}
