using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ReferenceChangeSetParserTests
{
    [Fact]
    public void A_create_Unit_array_parses_its_name_and_its_ordered_initial_aliases()
    {
        var parsed = ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateUnit,
            """[{"name":"Cardboard Box","aliases":"boxes, bx"},{"name":"Pallet"}]""",
            out var requests,
            out var code);

        Assert.True(parsed);
        Assert.Empty(code);
        Assert.Equal(2, requests.Count);
        Assert.Equal(1, requests[0].Order);
        Assert.Equal(ReferenceChangeKind.CreateUnit, requests[0].Kind);
        Assert.Equal("Cardboard Box", requests[0].Name);
        Assert.Equal(["boxes", "bx"], requests[0].Aliases);
        Assert.Equal(2, requests[1].Order);
        Assert.Equal("Pallet", requests[1].Name);
        Assert.Empty(requests[1].Aliases);
    }

    [Fact]
    public void Order_comes_from_the_position_in_the_array_never_from_the_element()
    {
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateLocation, """[{"name":"Shelf A","order":"7"}]""", out _, out _));
    }

    [Fact]
    public void An_element_carrying_a_property_this_kind_does_not_have_refuses_the_whole_array()
    {
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateLocation, """[{"name":"Shelf A","aliases":"shelf"}]""", out _, out var code));
        Assert.Equal("invalid_changes", code);
    }

    [Fact]
    public void A_kind_property_is_itself_unknown_so_a_mixed_batch_cannot_even_be_expressed()
    {
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RetireUnit, """[{"unit":"box","kind":"retire_location"}]""", out _, out _));
    }

    [Fact]
    public void A_non_string_value_refuses_the_whole_array()
    {
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateLocation, """[{"name":123}]""", out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""[{"name":"Shelf A"},"Shelf B"]""")]
    public void Anything_that_is_not_a_non_empty_array_of_objects_is_refused(string? json)
    {
        Assert.False(ReferenceChangeSetParser.TryParse(ReferenceChangeKind.CreateLocation, json, out var requests, out var code));
        Assert.Empty(requests);
        Assert.Equal("invalid_changes", code);
    }

    [Fact]
    public void More_changes_than_a_Participant_can_review_are_refused_by_their_own_code()
    {
        var elements = Enumerable
            .Range(1, ConfirmationProposal.MaxChanges + 1)
            .Select(index => $$"""{"name":"Shelf {{index}}"}""");

        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateLocation, $"[{string.Join(",", elements)}]", out _, out var code));
        Assert.Equal("too_many_changes", code);
    }

    [Theory]
    [InlineData(ReferenceChangeKind.CreateUnit, """[{"aliases":"boxes"}]""")]
    [InlineData(ReferenceChangeKind.CreateLocation, "[{}]")]
    [InlineData(ReferenceChangeKind.RenameUnit, """[{"unit":"box"}]""")]
    [InlineData(ReferenceChangeKind.RenameUnit, """[{"newName":"Carton"}]""")]
    [InlineData(ReferenceChangeKind.RenameLocation, """[{"location":"Shelf A"}]""")]
    [InlineData(ReferenceChangeKind.AddUnitAlias, """[{"unit":"box"}]""")]
    [InlineData(ReferenceChangeKind.RemoveUnitAlias, """[{"alias":"boxes"}]""")]
    [InlineData(ReferenceChangeKind.RetireUnit, "[{}]")]
    [InlineData(ReferenceChangeKind.RetireLocation, "[{}]")]
    public void Every_kind_refuses_an_element_missing_something_it_requires(ReferenceChangeKind kind, string json) =>
        Assert.False(ReferenceChangeSetParser.TryParse(kind, json, out _, out _));

    [Fact]
    public void A_blank_required_value_is_as_absent_as_a_missing_one() =>
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateLocation, """[{"name":"   "}]""", out _, out _));

    [Fact]
    public void An_aliases_property_that_lists_nothing_is_refused_rather_than_read_as_no_aliases() =>
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateUnit, """[{"name":"Box","aliases":" , "}]""", out _, out _));

    [Fact]
    public void Every_mutating_kind_parses_its_own_shape()
    {
        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RenameUnit, """[{"unit":"box","newName":"Carton"}]""", out var renameUnit, out _));
        Assert.Equal("box", renameUnit[0].Reference);
        Assert.Equal("Carton", renameUnit[0].NewName);

        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.AddUnitAlias, """[{"unit":"box","alias":"cartons"}]""", out var addAlias, out _));
        Assert.Equal("box", addAlias[0].Reference);
        Assert.Equal("cartons", addAlias[0].Alias);

        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RemoveUnitAlias, """[{"unit":"box","alias":"cartons"}]""", out var removeAlias, out _));
        Assert.Equal("cartons", removeAlias[0].Alias);

        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RetireUnit, """[{"unit":"box"}]""", out var retireUnit, out _));
        Assert.Equal("box", retireUnit[0].Reference);

        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RenameLocation, """[{"location":"Shelf A","newName":"Aisle 3"}]""", out var renameLocation, out _));
        Assert.Equal("Shelf A", renameLocation[0].Reference);
        Assert.Equal("Aisle 3", renameLocation[0].NewName);

        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RetireLocation, """[{"location":"Shelf A"}]""", out var retireLocation, out _));
        Assert.Equal("Shelf A", retireLocation[0].Reference);
    }
}
