using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public sealed class StockChangeSetParserTests
{
    [Fact]
    public void A_well_formed_batch_parses_into_ordered_requests()
    {
        const string Json = """
        [
          {"kind":"add","reference":"Steel Bolts","quantity":"5","note":"Blue box"},
          {"kind":"move","reference":"Brass Rivets","all":"true","to":"Shelf A"},
          {"kind":"rename","reference":"Old Name","newName":"New Name"},
          {"kind":"forget","reference":"Empty Thing","unlocated":"true"}
        ]
        """;

        Assert.True(StockChangeSetParser.TryParse(Json, out var requests, out var code));

        Assert.Equal(string.Empty, code);
        Assert.Equal(4, requests.Count);
        Assert.Equal([1, 2, 3, 4], requests.Select(r => r.Order));
        Assert.Equal(StockMutationKind.Add, requests[0].Kind);
        Assert.Equal("Steel Bolts", requests[0].Reference);
        Assert.Equal("5", requests[0].QuantityText);
        Assert.Equal("Blue box", requests[0].Note);
        Assert.Equal(StockMutationKind.Move, requests[1].Kind);
        Assert.True(requests[1].MoveAll);
        Assert.Equal("Shelf A", requests[1].DestinationLocationReference);
        Assert.Equal("New Name", requests[2].NewName);
        Assert.True(requests[3].UnlocatedOnly);
    }

    [Fact]
    public void A_move_to_the_unlocated_state_is_stated_as_its_own_flag_rather_than_a_Location_named_unlocated()
    {
        Assert.True(StockChangeSetParser.TryParse("""[{"kind":"move","reference":"X","all":"true","toUnlocated":"true"}]""", out var requests, out _));

        Assert.True(requests[0].DestinationUnlocated);
        Assert.Null(requests[0].DestinationLocationReference);
    }

    [Theory]
    [InlineData(null, "invalid_changes")]
    [InlineData("", "invalid_changes")]
    [InlineData("   ", "invalid_changes")]
    [InlineData("not json", "invalid_changes")]
    [InlineData("{}", "invalid_changes")]
    [InlineData("[]", "invalid_changes")]
    [InlineData("""["add"]""", "invalid_changes")]
    [InlineData("""[{"reference":"X"}]""", "invalid_changes")]
    [InlineData("""[{"kind":"Add","reference":"X"}]""", "invalid_changes")]
    [InlineData("""[{"kind":"destroy","reference":"X"}]""", "invalid_changes")]
    [InlineData("""[{"kind":"add","reference":"X","participantId":"me"}]""", "invalid_changes")]
    [InlineData("""[{"kind":"add","reference":"X","inventoryId":"other"}]""", "invalid_changes")]
    [InlineData("""[{"kind":"add","reference":"X","quantity":5}]""", "invalid_changes")]
    [InlineData("""[{"kind":"add","reference":"X","quantity":null}]""", "invalid_changes")]
    [InlineData("""[{"kind":"add","reference":"X","nested":{"a":"b"}}]""", "invalid_changes")]
    public void Anything_that_is_not_exactly_the_agreed_shape_is_refused_rather_than_partly_understood(string? json, string expectedCode)
    {
        Assert.False(StockChangeSetParser.TryParse(json, out var requests, out var code));

        Assert.Empty(requests);
        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public void A_batch_larger_than_a_Participant_can_review_is_refused_on_its_own_terms()
    {
        var elements = string.Join(",", Enumerable.Range(0, ConfirmationProposal.MaxChanges + 1)
            .Select(i => $$"""{"kind":"add","reference":"Thing {{i}}","quantity":"1"}"""));

        Assert.False(StockChangeSetParser.TryParse($"[{elements}]", out var requests, out var code));

        Assert.Empty(requests);
        Assert.Equal("too_many_changes", code);
    }

    [Fact]
    public void A_batch_of_exactly_the_maximum_size_still_parses()
    {
        var elements = string.Join(",", Enumerable.Range(0, ConfirmationProposal.MaxChanges)
            .Select(i => $$"""{"kind":"add","reference":"Thing {{i}}","quantity":"1"}"""));

        Assert.True(StockChangeSetParser.TryParse($"[{elements}]", out var requests, out _));

        Assert.Equal(ConfirmationProposal.MaxChanges, requests.Count);
    }

    [Fact]
    public void Flags_are_only_ever_an_explicit_true_so_stray_text_can_never_widen_a_change()
    {
        Assert.True(StockChangeSetParser.TryParse("""[{"kind":"forget","reference":"X","unlocated":"yes"}]""", out var requests, out _));

        Assert.False(requests[0].UnlocatedOnly);
    }
}
