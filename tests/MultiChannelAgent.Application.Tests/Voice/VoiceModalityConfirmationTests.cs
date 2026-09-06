using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceModalityConfirmationTests
{
    private static readonly ParticipantId Alice = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000001"));
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static InboundTurn CreateTurn(string contentText, InputModality modality, bool wasInterrupted = false) =>
        InboundTurn.Create(new InboundTurnDraft
        {
            NativeMessageId = Guid.NewGuid().ToString(),
            ParticipantId = Alice,
            ChannelConversationId = "conv-1",
            Channel = "web",
            Principal = ChannelPrincipal.EntraUser(Alice.Value.ToString(), null),
            Capabilities = ChannelCapabilities.Text | ChannelCapabilities.RichText | ChannelCapabilities.ProgressEvents,
            ContentParts = [TurnContentPart.Create(1, ContentProvenance.Direct, contentText)],
            ReceivedAt = Now,
            InputModality = modality,
            WasInterrupted = wasInterrupted,
        });

    [Fact]
    public void Voice_modality_confirm_returns_None_regardless_of_content()
    {
        var turn = CreateTurn("confirm ABC_token_placeholder_43chars_here1234567", InputModality.Voice);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Voice_modality_reject_returns_None()
    {
        var turn = CreateTurn("reject", InputModality.Voice);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Voice_modality_yes_returns_None()
    {
        var turn = CreateTurn("yes", InputModality.Voice);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Voice_modality_ordinary_request_returns_None()
    {
        var turn = CreateTurn("add five boxes of gloves", InputModality.Voice);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Text_modality_confirm_returns_Confirmed()
    {
        var turn = CreateTurn("confirm ABC_token_placeholder_43chars_here1234567", InputModality.Text);
        Assert.Equal(DirectConfirmationEvidence.Confirmed, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Text_modality_reject_returns_Rejected()
    {
        var turn = CreateTurn("reject", InputModality.Text);
        Assert.Equal(DirectConfirmationEvidence.Rejected, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Text_modality_interrupted_returns_None()
    {
        var turn = CreateTurn("confirm ABC_token_placeholder_43chars_here1234567", InputModality.Text, wasInterrupted: true);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Text_modality_ordinary_request_returns_None()
    {
        var turn = CreateTurn("add five boxes of gloves", InputModality.Text);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }
}
