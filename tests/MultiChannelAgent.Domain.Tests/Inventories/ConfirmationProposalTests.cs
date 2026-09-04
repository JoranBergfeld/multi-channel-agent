using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public sealed class ConfirmationProposalTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly InventoryId Inventory = new(Guid.NewGuid());
    private static readonly ParticipantId Participant = new(Guid.NewGuid());
    private static readonly TurnId Turn = TurnId.NewId();
    private const string Conversation = "web:profile-1";

    private static ProposedEntryState Entry(Quantity previous, Quantity resulting, bool retired = false, StockEntryId? id = null) => new(
        id ?? new StockEntryId(Guid.NewGuid()),
        "Steel Bolts",
        "steel bolts",
        new UnitId(Guid.NewGuid()),
        "each",
        LocationId: null,
        LocationName: null,
        Note: null,
        previous,
        resulting,
        retired);

    private static ProposedChange ForgetChange(StockEntryId id) => new()
    {
        Order = 1,
        Kind = StockMutationKind.Forget,
        Effect = StockChangeEffectKind.Forgotten,
        Source = Entry(Quantity.Zero, Quantity.Zero, retired: true, id),
    };

    private static ConfirmationProposal Create(
        IReadOnlyList<ProposedChange> changes,
        IReadOnlyList<ExpectedEntryVersion> versions,
        IReadOnlyList<ExpectedEquivalentStockAbsence>? absences = null) =>
        ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            Participant,
            Conversation,
            Inventory,
            Turn,
            changes,
            versions,
            absences ?? [],
            Now);

    [Fact]
    public void A_proposal_expires_exactly_ten_minutes_after_it_was_created()
    {
        var id = new StockEntryId(Guid.NewGuid());
        var proposal = Create([ForgetChange(id)], [new ExpectedEntryVersion(id, Guid.NewGuid())]);

        Assert.Equal(10, ConfirmationProposal.LifetimeMinutes);
        Assert.Equal(Now.AddMinutes(10), proposal.ExpiresAt);
        Assert.False(proposal.IsExpired(Now.AddMinutes(9).AddSeconds(59)));
        Assert.True(proposal.IsExpired(Now.AddMinutes(10)));
        Assert.True(proposal.IsExpired(Now.AddMinutes(11)));
    }

    [Fact]
    public void A_proposal_is_bound_to_one_Participant_one_conversation_and_one_Inventory()
    {
        var id = new StockEntryId(Guid.NewGuid());
        var proposal = Create([ForgetChange(id)], [new ExpectedEntryVersion(id, Guid.NewGuid())]);

        Assert.True(proposal.BelongsTo(Participant, Conversation, Inventory));
        Assert.False(proposal.BelongsTo(new ParticipantId(Guid.NewGuid()), Conversation, Inventory));
        Assert.False(proposal.BelongsTo(Participant, "web:profile-2", Inventory));
        Assert.False(proposal.BelongsTo(Participant, Conversation, new InventoryId(Guid.NewGuid())));
    }

    [Fact]
    public void A_proposals_execution_identity_comes_from_the_proposal_not_from_who_confirms_it()
    {
        var id = new StockEntryId(Guid.NewGuid());
        var proposal = Create([ForgetChange(id)], [new ExpectedEntryVersion(id, Guid.NewGuid())]);

        Assert.Equal(StockOperationId.DeriveForProposal(proposal.Id), proposal.ExecutionOperationId);
        Assert.NotEqual(default, proposal.ExecutionOperationId.Value);
    }

    [Fact]
    public void A_proposal_must_carry_at_least_one_change()
    {
        Assert.Throws<ArgumentException>(() => Create([], []));
    }

    [Fact]
    public void A_proposal_may_not_carry_more_changes_than_a_Participant_can_review()
    {
        var changes = Enumerable.Range(1, ConfirmationProposal.MaxChanges + 1)
            .Select(order => ForgetChange(new StockEntryId(Guid.NewGuid())) with { Order = order })
            .ToList();
        var versions = changes.Select(c => new ExpectedEntryVersion(c.Source.StockEntryId!.Value, Guid.NewGuid())).ToList();

        Assert.Equal(25, ConfirmationProposal.MaxChanges);
        Assert.Throws<ArgumentException>(() => Create(changes, versions));
    }

    [Fact]
    public void A_proposals_changes_must_be_ordered_uniquely_so_execution_order_is_never_ambiguous()
    {
        var first = ForgetChange(new StockEntryId(Guid.NewGuid()));
        var second = ForgetChange(new StockEntryId(Guid.NewGuid()));
        var versions = new[]
        {
            new ExpectedEntryVersion(first.Source.StockEntryId!.Value, Guid.NewGuid()),
            new ExpectedEntryVersion(second.Source.StockEntryId!.Value, Guid.NewGuid()),
        };

        Assert.Throws<ArgumentException>(() => Create([first, second], versions));
    }

    [Fact]
    public void Every_existing_Stock_Entry_a_proposal_touches_must_carry_an_expected_version()
    {
        var id = new StockEntryId(Guid.NewGuid());

        // No expected version at all: executing this could overwrite a change made since.
        Assert.Throws<ArgumentException>(() => Create([ForgetChange(id)], []));
    }

    [Fact]
    public void A_proposal_that_only_creates_Stock_needs_an_expected_absence_rather_than_a_version()
    {
        var unitId = new UnitId(Guid.NewGuid());
        var create = new ProposedChange
        {
            Order = 1,
            Kind = StockMutationKind.Add,
            Effect = StockChangeEffectKind.Created,
            Source = new ProposedEntryState(
                StockEntryId: null,
                "Brass Rivets",
                "brass rivets",
                unitId,
                "each",
                LocationId: null,
                LocationName: null,
                Note: null,
                Quantity.Zero,
                Quantity.Create(4m),
                Retired: false),
        };

        var proposal = Create([create], [], [new ExpectedEquivalentStockAbsence("brass rivets", unitId, null)]);

        Assert.Single(proposal.Changes);
        Assert.Empty(proposal.ExpectedVersions);
        Assert.Single(proposal.ExpectedAbsences);
    }

    [Fact]
    public void A_proposal_reports_the_survivor_and_the_retired_source_of_every_merge()
    {
        var sourceId = new StockEntryId(Guid.NewGuid());
        var destinationId = new StockEntryId(Guid.NewGuid());
        var merge = new ProposedChange
        {
            Order = 1,
            Kind = StockMutationKind.Rename,
            Effect = StockChangeEffectKind.RenameMerged,
            Source = Entry(Quantity.Create(4m), Quantity.Zero, retired: true, sourceId),
            Destination = Entry(Quantity.Create(6m), Quantity.Create(10m), retired: false, destinationId),
            TransferredQuantity = Quantity.Create(4m),
            NewName = "Brass Rivets",
            NewNormalizedName = "brass rivets",
        };

        var proposal = Create(
            [merge],
            [new ExpectedEntryVersion(sourceId, Guid.NewGuid()), new ExpectedEntryVersion(destinationId, Guid.NewGuid())]);

        var change = Assert.Single(proposal.Changes);
        Assert.Equal(destinationId, change.SurvivingStockEntryId);
        Assert.Equal(sourceId, change.RetiredStockEntryId);
        Assert.True(change.RetiresSource);
    }

    [Fact]
    public void A_change_that_retires_nothing_reports_its_own_Stock_Entry_as_the_survivor()
    {
        var id = new StockEntryId(Guid.NewGuid());
        var rename = new ProposedChange
        {
            Order = 1,
            Kind = StockMutationKind.Rename,
            Effect = StockChangeEffectKind.Renamed,
            Source = Entry(Quantity.Create(4m), Quantity.Create(4m), retired: false, id),
            NewName = "Brass Rivets",
            NewNormalizedName = "brass rivets",
        };

        var proposal = Create([rename], [new ExpectedEntryVersion(id, Guid.NewGuid())]);

        var change = Assert.Single(proposal.Changes);
        Assert.Equal(id, change.SurvivingStockEntryId);
        Assert.Null(change.RetiredStockEntryId);
    }
}
