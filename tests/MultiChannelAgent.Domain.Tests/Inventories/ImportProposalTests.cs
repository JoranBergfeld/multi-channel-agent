using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ImportProposalTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly InventoryId Inventory = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly UnitId Each = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private static ImportEntry Entry(string name = "Steel Bolts", int lineNumber = 2) => new()
    {
        LineNumber = lineNumber,
        SourceLineNumbers = [lineNumber],
        Name = name,
        NormalizedName = NameNormalization.Normalize(name),
        Quantity = Quantity.Create(4m),
        UnitId = Each,
        UnitCanonicalName = "each",
        LocationId = null,
        LocationName = null,
        Note = null,
    };

    private static ImportProposal Create(IReadOnlyList<ImportEntry>? entries = null) => ImportProposal.Create(
        ConfirmationToken.HashOf(ConfirmationToken.Issue()),
        Participant,
        Inventory,
        FileDigest.Of("Name,Quantity,Unit,Location,Note\n"u8.ToArray()),
        entries ?? [Entry()],
        EmptyStateVersion.Empty,
        Now);

    [Fact]
    public void A_proposal_carries_the_exact_entries_it_was_previewed_with()
    {
        var proposal = Create([Entry("Steel Bolts"), Entry("Brass Rivets", 3)]);

        Assert.Equal(["Steel Bolts", "Brass Rivets"], proposal.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void A_proposal_is_immune_to_mutation_of_the_source_list_after_creation()
    {
        var source = new List<ImportEntry> { Entry("Steel Bolts") };
        var proposal = Create(source);

        source.Add(Entry("Brass Rivets", 3));

        Assert.Equal(["Steel Bolts"], proposal.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void A_proposal_shares_the_shipped_ten_minute_single_use_lifetime()
    {
        var proposal = Create();

        Assert.Equal(ConfirmationProposal.LifetimeMinutes, ImportProposal.LifetimeMinutes);
        Assert.Equal(Now.AddMinutes(ImportProposal.LifetimeMinutes), proposal.ExpiresAt);
        Assert.False(proposal.IsExpired(proposal.ExpiresAt.AddTicks(-1)));
        Assert.True(proposal.IsExpired(proposal.ExpiresAt));
    }

    [Fact]
    public void A_proposal_belongs_to_exactly_one_Participant_and_one_Inventory()
    {
        var proposal = Create();

        Assert.True(proposal.BelongsTo(Participant, Inventory));
        Assert.False(proposal.BelongsTo(new ParticipantId(Guid.NewGuid()), Inventory));
        Assert.False(proposal.BelongsTo(Participant, new InventoryId(Guid.NewGuid())));
    }

    [Fact]
    public void A_proposal_must_carry_at_least_one_entry() =>
        Assert.Throws<ArgumentException>(() => Create([]));

    [Fact]
    public void A_proposal_must_not_exceed_the_normalized_entry_bound()
    {
        var entries = Enumerable
            .Range(0, ImportContract.MaxNormalizedEntries + 1)
            .Select(index => Entry($"Item {index}", index + 2))
            .ToList();

        Assert.Throws<ArgumentException>(() => Create(entries));
    }

    [Fact]
    public void A_proposal_at_exactly_the_normalized_entry_bound_is_accepted()
    {
        var entries = Enumerable
            .Range(0, ImportContract.MaxNormalizedEntries)
            .Select(index => Entry($"Item {index}", index + 2))
            .ToList();

        var proposal = Create(entries);

        Assert.Equal(ImportContract.MaxNormalizedEntries, proposal.Entries.Count);
    }

    [Fact]
    public void The_empty_state_version_says_only_that_the_Inventory_held_nothing()
    {
        Assert.Equal(0, EmptyStateVersion.Empty.ExpectedStockEntryCount);
        Assert.Equal(EmptyStateVersion.Empty, Create().EmptyStateVersion);
    }

    [Fact]
    public void A_proposal_executes_under_its_own_ledger_identity_which_no_other_ledger_can_mint()
    {
        var proposal = Create();

        Assert.Equal(ImportOperationId.DeriveForProposal(proposal.Id), proposal.ExecutionOperationId);
        Assert.NotEqual(
            StockOperationId.DeriveForProposal(new ProposalId(proposal.Id.Value)).Value,
            proposal.ExecutionOperationId.Value);
        Assert.NotEqual(
            ReferenceOperationId.DeriveForProposal(new ProposalId(proposal.Id.Value)).Value,
            proposal.ExecutionOperationId.Value);
    }

    [Fact]
    public void A_proposals_execution_identity_is_derived_and_therefore_survives_a_restart()
    {
        var proposal = Create();

        Assert.Equal(ImportOperationId.DeriveForProposal(proposal.Id), ImportOperationId.DeriveForProposal(proposal.Id));
    }

    [Fact]
    public void A_proposal_names_every_reference_its_entries_depend_on()
    {
        var locationId = new LocationId(Guid.NewGuid());
        var located = Entry() with { LocationId = locationId, LocationName = "Shelf A" };

        var proposal = Create([Entry(), located]);

        Assert.Equal([Each], proposal.ReferencedUnitIds);
        Assert.Equal([locationId], proposal.ReferencedLocationIds);
    }

    [Fact]
    public void Referenced_ids_are_distinct_and_kept_in_first_seen_order()
    {
        var firstUnit = new UnitId(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var secondUnit = new UnitId(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var firstLocation = new LocationId(Guid.Parse("66666666-6666-6666-6666-666666666666"));
        var secondLocation = new LocationId(Guid.Parse("77777777-7777-7777-7777-777777777777"));

        var entries = new[]
        {
            Entry("A", 2) with { UnitId = secondUnit, LocationId = secondLocation },
            Entry("B", 3) with { UnitId = firstUnit, LocationId = firstLocation },
            Entry("C", 4) with { UnitId = secondUnit, LocationId = secondLocation },
        };

        var proposal = Create(entries);

        Assert.Equal([secondUnit, firstUnit], proposal.ReferencedUnitIds);
        Assert.Equal([secondLocation, firstLocation], proposal.ReferencedLocationIds);
    }

    [Fact]
    public void An_entry_with_no_Location_contributes_nothing_to_the_referenced_Locations()
    {
        var proposal = Create([Entry()]);

        Assert.Empty(proposal.ReferencedLocationIds);
    }

    [Fact]
    public void Two_proposals_never_share_an_identity() =>
        Assert.NotEqual(Create().Id, Create().Id);
}
