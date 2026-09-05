using MultiChannelAgent.Application.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class FakeVoiceLiveGatewayTests
{
    [Fact]
    public async Task Negotiation_returns_a_non_empty_control_session_id_and_sdp_answer()
    {
        var fake = new FakeVoiceLiveGateway();
        var request = new VoiceLiveNegotiationRequest("v=0\r\no=caller 123\r\n");

        var result = await fake.NegotiateAsync(request, CancellationToken.None);

        Assert.NotEmpty(result.ControlSessionId);
        Assert.NotEmpty(result.SdpAnswer);
    }

    [Fact]
    public async Task Negotiated_session_is_owned()
    {
        var fake = new FakeVoiceLiveGateway();

        var result = await fake.NegotiateAsync(new VoiceLiveNegotiationRequest("v=0\r\n"), CancellationToken.None);

        Assert.True(fake.OwnsSession(result.ControlSessionId));
    }

    [Fact]
    public async Task Terminated_session_is_no_longer_owned()
    {
        var fake = new FakeVoiceLiveGateway();
        var result = await fake.NegotiateAsync(new VoiceLiveNegotiationRequest("v=0\r\n"), CancellationToken.None);

        await fake.TerminateAsync(result.ControlSessionId, CancellationToken.None);

        Assert.False(fake.OwnsSession(result.ControlSessionId));
    }

    [Fact]
    public void Unknown_session_is_not_owned()
    {
        var fake = new FakeVoiceLiveGateway();

        Assert.False(fake.OwnsSession("unknown-session-id"));
    }

    [Fact]
    public async Task One_shot_failure_resets_after_triggering()
    {
        var fake = new FakeVoiceLiveGateway
        {
            NextNegotiationFailure = new InvalidOperationException("Synthetic failure.")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fake.NegotiateAsync(new VoiceLiveNegotiationRequest("v=0\r\n"), CancellationToken.None));

        var result = await fake.NegotiateAsync(new VoiceLiveNegotiationRequest("v=0\r\n"), CancellationToken.None);

        Assert.NotEmpty(result.ControlSessionId);
    }

    [Fact]
    public async Task Sdp_answer_template_is_used_in_negotiation_result()
    {
        const string expectedAnswer = "v=0\r\no=server 42\r\n";
        var fake = new FakeVoiceLiveGateway { SdpAnswerTemplate = expectedAnswer };

        var result = await fake.NegotiateAsync(new VoiceLiveNegotiationRequest("v=0\r\n"), CancellationToken.None);

        Assert.Equal(expectedAnswer, result.SdpAnswer);
    }
}
