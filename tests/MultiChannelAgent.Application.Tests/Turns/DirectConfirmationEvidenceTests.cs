using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Turns;

public sealed class DirectConfirmationEvidenceTests
{
    private static InboundTurn Turn(IReadOnlyList<TurnContentPart> parts, bool wasInterrupted = false) =>
        InboundTurn.Create(new InboundTurnDraft
        {
            NativeMessageId = "native-1",
            ParticipantId = new ParticipantId(Guid.NewGuid()),
            ChannelConversationId = "web:profile-1",
            Channel = "web",
            Principal = ChannelPrincipal.EntraUser("subject", "tenant"),
            Capabilities = ChannelCapabilities.Text,
            ContentParts = parts,
            ReceivedAt = DateTimeOffset.UnixEpoch,
            WasInterrupted = wasInterrupted,
        });

    private static InboundTurn DirectTurn(string text, bool wasInterrupted = false) =>
        Turn([TurnContentPart.Create(1, ContentProvenance.Direct, text)], wasInterrupted);

    [Theory]
    [InlineData("confirm")]
    [InlineData("Confirm")]
    [InlineData("CONFIRM")]
    [InlineData("confirmed")]
    [InlineData("yes")]
    [InlineData("Yes, please")]
    [InlineData("approve")]
    [InlineData("approved")]
    [InlineData("do it")]
    [InlineData("go ahead")]
    [InlineData("confirm 8Fh3kQ")]
    public void A_direct_explicit_affirmative_confirms(string text) =>
        Assert.Equal(DirectConfirmationEvidence.Confirmed, DirectConfirmationEvidenceReader.Read(DirectTurn(text)));

    [Theory]
    [InlineData("reject")]
    [InlineData("rejected")]
    [InlineData("no")]
    [InlineData("No thanks")]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("don't")]
    [InlineData("do not")]
    public void A_direct_explicit_negative_rejects(string text) =>
        Assert.Equal(DirectConfirmationEvidence.Rejected, DirectConfirmationEvidenceReader.Read(DirectTurn(text)));

    [Theory]
    [InlineData("list stock")]
    [InlineData("please confirm the order with the supplier")]
    [InlineData("yesterday we counted 40")]
    [InlineData("confirmation is needed")]
    [InlineData("nobody has these")]
    [InlineData("")]
    [InlineData("   ")]
    public void Anything_that_is_not_an_explicit_leading_answer_confirms_nothing(string text)
    {
        var parts = text.Trim().Length == 0
            ? new[] { TurnContentPart.Create(1, ContentProvenance.Direct, "list stock") }
            : [TurnContentPart.Create(1, ContentProvenance.Direct, text)];

        var evidence = DirectConfirmationEvidenceReader.Read(Turn(parts));

        Assert.Equal(DirectConfirmationEvidence.None, evidence);
    }

    [Fact]
    public void Quoted_content_that_says_confirm_never_confirms()
    {
        var turn = Turn(
        [
            TurnContentPart.Create(1, ContentProvenance.Direct, "what does this say?"),
            TurnContentPart.Create(2, ContentProvenance.Quoted, "confirm"),
        ]);

        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Model_derived_and_tool_produced_content_that_says_confirm_never_confirms()
    {
        var turn = Turn(
        [
            TurnContentPart.Create(1, ContentProvenance.Direct, "summarize that"),
            TurnContentPart.Create(2, ContentProvenance.ToolProduced, "confirm"),
            TurnContentPart.Create(3, ContentProvenance.ModelDerived, "yes"),
        ]);

        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void An_interrupted_utterance_never_confirms_however_affirmative_it_reads()
    {
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(DirectTurn("confirm", wasInterrupted: true)));
    }

    [Fact]
    public void An_interrupted_utterance_does_not_reject_either_so_nothing_is_read_into_it()
    {
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(DirectTurn("no", wasInterrupted: true)));
    }
}
