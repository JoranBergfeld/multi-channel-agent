using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public sealed class InventoryConfirmationServiceTests
{
    private static readonly ParticipantId Editor = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Viewer = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly ParticipantId Owner = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly InventoryId SomeInventory = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly InventoryId AnotherInventory = new(Guid.Parse("bbbbbbbb-4444-4444-4444-444444444444"));
    private static readonly UnitId EachUnit = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly LocationId ShelfA = new(Guid.Parse("77777777-7777-7777-7777-777777777777"));
    private static readonly StockOperationId SomeOperation = new(Guid.Parse("99999999-9999-9999-9999-999999999999"));
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private const string Conversation = "conversation-1";

    private sealed record Harness(
        StockChangeSetService ChangeSets,
        InventoryConfirmationService Confirmations,
        InMemoryStockStore StockStore,
        InMemoryConfirmationProposalStore ProposalStore,
        InMemoryStockChangeSetStore ChangeSetStore);

    private static Harness CreateHarness()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Editor, MembershipRole.Editor, Now);
        inventoryStore.GrantMembership(SomeInventory, Viewer, MembershipRole.Viewer, Now);
        inventoryStore.GrantMembership(AnotherInventory, Editor, MembershipRole.Editor, Now);

        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);

        var stockStore = new InMemoryStockStore();
        var referenceStore = new InMemoryInventoryReferenceStore();
        referenceStore.AddUnit(SomeInventory, EachUnit, "each", "piece", "pieces", "pc", "pcs");
        referenceStore.AddLocation(SomeInventory, ShelfA, "Shelf A");

        var proposalStore = new InMemoryConfirmationProposalStore();
        var changeSetStore = new InMemoryStockChangeSetStore(stockStore, proposalStore);

        return new Harness(
            new StockChangeSetService(
                new StockChangeResolver(stockStore, referenceStore), changeSetStore, proposalStore, authorizationService),
            new InventoryConfirmationService(
                proposalStore, changeSetStore, new InMemoryReferenceAdministrationStore(proposalStore), authorizationService),
            stockStore,
            proposalStore,
            changeSetStore);
    }

    private static StockEntrySummary Seed(Harness harness, string name, string quantity, LocationId? locationId = null) =>
        harness.StockStore.CreateRow(
            SomeInventory,
            name,
            EachUnit,
            "each",
            locationId,
            locationId == ShelfA ? "Shelf A" : null,
            note: null,
            Quantity.Create(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Seeds a merge-retiring Move and proposes it, returning the token the Participant was handed.</summary>
    private static async Task<(string Token, StockEntrySummary Source, StockEntrySummary Destination)> ProposeMergeAsync(
        Harness harness, ParticipantId? participantId = null)
    {
        var source = Seed(harness, "Steel Bolts", "10");
        var destination = Seed(harness, "Steel Bolts", "4", ShelfA);

        var result = await harness.ChangeSets.ApplyAsync(
            participantId ?? Editor,
            SomeInventory,
            TurnId.NewId(),
            SomeOperation,
            [
                new StockChangeRequest
                {
                    Order = 1,
                    Kind = StockMutationKind.Move,
                    Reference = "Steel Bolts",
                    UnlocatedOnly = true,
                    MoveAll = true,
                    DestinationLocationReference = "Shelf A",
                },
            ],
            Conversation,
            Now,
            CancellationToken.None);

        Assert.Equal(StockChangeSetResultKind.ConfirmationRequired, result.Kind);
        harness.ChangeSetStore.AuditFacts.Clear();

        return (result.Proposal!.Token, source, destination);
    }

    private static Task<InventoryConfirmationResult> ConfirmAsync(
        Harness harness,
        string? token,
        DirectConfirmationEvidence evidence = DirectConfirmationEvidence.Confirmed,
        ParticipantId? participantId = null,
        InventoryId? inventoryId = null,
        string? conversation = null,
        TurnId? turnId = null,
        DateTimeOffset? now = null) =>
        harness.Confirmations.ConfirmAsync(
            participantId ?? Editor,
            inventoryId ?? SomeInventory,
            turnId ?? TurnId.NewId(),
            token,
            evidence,
            conversation ?? Conversation,
            now ?? Now,
            CancellationToken.None);

    private static Task<InventoryConfirmationResult> RejectAsync(
        Harness harness,
        string? token,
        DirectConfirmationEvidence evidence = DirectConfirmationEvidence.Rejected,
        ParticipantId? participantId = null) =>
        harness.Confirmations.RejectAsync(
            participantId ?? Editor,
            SomeInventory,
            TurnId.NewId(),
            token,
            evidence,
            Conversation,
            Now,
            CancellationToken.None);

    [Fact]
    public async Task Direct_explicit_confirmation_executes_the_stored_proposal_exactly()
    {
        var harness = CreateHarness();
        var (token, source, destination) = await ProposeMergeAsync(harness);
        var pendingId = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!.Id;

        var result = await ConfirmAsync(harness, token);

        Assert.Equal(InventoryConfirmationResultKind.Completed, result.Kind);
        var change = Assert.Single(result.Applied!.Changes);
        Assert.Equal("merged", change.Effect);
        Assert.Equal(destination.Id.ToString(), change.SurvivingStockEntryId);
        Assert.Equal(source.Id.ToString(), change.RetiredStockEntryId);
        Assert.Single(harness.ChangeSetStore.AuditFacts);
        Assert.Equal("14", harness.StockStore.Find(SomeInventory, destination.Id)!.Quantity.ToInvariantText());
        Assert.Null(harness.StockStore.Find(SomeInventory, source.Id));
        Assert.Equal(ProposalStatus.Confirmed, await harness.ProposalStore.FindStatusAsync(pendingId, CancellationToken.None));
    }

    [Fact]
    public async Task Executing_a_proposal_consumes_it_so_a_second_confirmation_finds_nothing_pending()
    {
        var harness = CreateHarness();
        var (token, _, _) = await ProposeMergeAsync(harness);

        Assert.Equal(InventoryConfirmationResultKind.Completed, (await ConfirmAsync(harness, token)).Kind);

        var second = await ConfirmAsync(harness, token);

        Assert.Equal(InventoryConfirmationResultKind.NotFound, second.Kind);
        Assert.Equal("proposal_not_found", second.Code);
        Assert.Single(harness.ChangeSetStore.AuditFacts);
    }

    [Fact]
    public async Task Confirming_without_direct_explicit_evidence_executes_nothing_and_leaves_the_proposal_pending()
    {
        var harness = CreateHarness();
        var (token, source, _) = await ProposeMergeAsync(harness);
        var pendingId = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!.Id;

        var result = await ConfirmAsync(harness, token, DirectConfirmationEvidence.None);

        Assert.Equal(InventoryConfirmationResultKind.Invalid, result.Kind);
        Assert.Equal("confirmation_evidence_missing", result.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
        Assert.Equal(pendingId, (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task A_wrong_token_executes_nothing_and_leaves_the_proposal_pending()
    {
        var harness = CreateHarness();
        var (_, source, _) = await ProposeMergeAsync(harness);
        var pendingId = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!.Id;

        var result = await ConfirmAsync(harness, ConfirmationToken.Issue());

        Assert.Equal(InventoryConfirmationResultKind.Invalid, result.Kind);
        Assert.Equal("proposal_token_mismatch", result.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
        Assert.Equal(pendingId, (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task A_malformed_token_is_refused_exactly_like_a_wrong_one()
    {
        var harness = CreateHarness();
        var (_, source, _) = await ProposeMergeAsync(harness);

        var result = await ConfirmAsync(harness, "not-a-token");

        Assert.Equal(InventoryConfirmationResultKind.Invalid, result.Kind);
        Assert.Equal("proposal_token_mismatch", result.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task Confirming_after_ten_minutes_expires_the_proposal_and_executes_nothing()
    {
        var harness = CreateHarness();
        var (token, source, _) = await ProposeMergeAsync(harness);
        var pendingId = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!.Id;

        var result = await ConfirmAsync(harness, token, now: Now.AddMinutes(10));

        Assert.Equal(InventoryConfirmationResultKind.Conflict, result.Kind);
        Assert.Equal("proposal_expired", result.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
        Assert.Equal(ProposalStatus.Expired, await harness.ProposalStore.FindStatusAsync(pendingId, CancellationToken.None));
    }

    [Fact]
    public async Task Confirming_a_proposal_bound_to_another_Inventory_is_indistinguishable_from_no_proposal_at_all()
    {
        var harness = CreateHarness();
        var (token, source, _) = await ProposeMergeAsync(harness);

        var result = await ConfirmAsync(harness, token, inventoryId: AnotherInventory);

        Assert.Equal(InventoryConfirmationResultKind.NotFound, result.Kind);
        Assert.Equal("proposal_not_found", result.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task Confirming_from_another_conversation_is_indistinguishable_from_no_proposal_at_all()
    {
        var harness = CreateHarness();
        var (token, source, _) = await ProposeMergeAsync(harness);

        var result = await ConfirmAsync(harness, token, conversation: "conversation-2");

        Assert.Equal(InventoryConfirmationResultKind.NotFound, result.Kind);
        Assert.Equal("proposal_not_found", result.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task Confirming_a_proposal_that_was_superseded_finds_only_the_replacement()
    {
        var harness = CreateHarness();
        var (stale, source, _) = await ProposeMergeAsync(harness);
        var empty = Seed(harness, "Brass Rivets", "0");

        var replacement = await harness.ChangeSets.ApplyAsync(
            Editor,
            SomeInventory,
            TurnId.NewId(),
            SomeOperation,
            [new StockChangeRequest { Order = 1, Kind = StockMutationKind.Forget, Reference = "Brass Rivets" }],
            Conversation,
            Now,
            CancellationToken.None);

        var result = await ConfirmAsync(harness, stale);

        Assert.Equal(InventoryConfirmationResultKind.Invalid, result.Kind);
        Assert.Equal("proposal_token_mismatch", result.Code);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());

        // The replacement is the only thing "confirm" can now mean, and it still works.
        Assert.Equal(InventoryConfirmationResultKind.Completed, (await ConfirmAsync(harness, replacement.Proposal!.Token)).Kind);
        Assert.Null(harness.StockStore.Find(SomeInventory, empty.Id));
    }

    [Fact]
    public async Task A_proposal_whose_Stock_moved_underneath_it_conflicts_invalidates_and_changes_nothing()
    {
        var harness = CreateHarness();
        var (token, source, destination) = await ProposeMergeAsync(harness);
        var pendingId = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!.Id;
        harness.ChangeSetStore.ForceConflict = true;

        var result = await ConfirmAsync(harness, token);

        Assert.Equal(InventoryConfirmationResultKind.Conflict, result.Kind);
        Assert.Equal("state_changed", result.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
        Assert.Equal("4", harness.StockStore.Find(SomeInventory, destination.Id)!.Quantity.ToInvariantText());
        Assert.Equal(ProposalStatus.Conflicted, await harness.ProposalStore.FindStatusAsync(pendingId, CancellationToken.None));
    }

    [Fact]
    public async Task Direct_explicit_rejection_settles_the_proposal_and_changes_nothing()
    {
        var harness = CreateHarness();
        var (token, source, destination) = await ProposeMergeAsync(harness);
        var pendingId = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!.Id;

        var result = await RejectAsync(harness, token);

        Assert.Equal(InventoryConfirmationResultKind.Rejected, result.Kind);
        Assert.Equal("rejected", result.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
        Assert.Equal("4", harness.StockStore.Find(SomeInventory, destination.Id)!.Quantity.ToInvariantText());
        Assert.Equal(ProposalStatus.Rejected, await harness.ProposalStore.FindStatusAsync(pendingId, CancellationToken.None));

        // A rejected proposal can never later be confirmed.
        Assert.Equal(InventoryConfirmationResultKind.NotFound, (await ConfirmAsync(harness, token)).Kind);
    }

    [Fact]
    public async Task Rejecting_without_direct_explicit_evidence_settles_nothing()
    {
        var harness = CreateHarness();
        var (token, _, _) = await ProposeMergeAsync(harness);
        var pendingId = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!.Id;

        var result = await RejectAsync(harness, token, DirectConfirmationEvidence.None);

        Assert.Equal(InventoryConfirmationResultKind.Invalid, result.Kind);
        Assert.Equal("rejection_evidence_missing", result.Code);
        Assert.Equal(ProposalStatus.Pending, await harness.ProposalStore.FindStatusAsync(pendingId, CancellationToken.None));
    }

    [Fact]
    public async Task Rejecting_when_nothing_is_pending_is_answered_generically()
    {
        var harness = CreateHarness();

        var result = await RejectAsync(harness, token: null);

        Assert.Equal(InventoryConfirmationResultKind.NotFound, result.Kind);
        Assert.Equal("proposal_not_found", result.Code);
    }

    [Fact]
    public async Task A_Viewer_can_neither_confirm_nor_reject_and_learns_nothing_from_trying()
    {
        var harness = CreateHarness();
        var (token, source, _) = await ProposeMergeAsync(harness);

        var confirmation = await ConfirmAsync(harness, token, participantId: Viewer);
        var rejection = await RejectAsync(harness, token, participantId: Viewer);

        Assert.Equal(InventoryConfirmationResultKind.Forbidden, confirmation.Kind);
        Assert.Equal("forbidden", confirmation.Code);
        Assert.Equal(InventoryConfirmationResultKind.Forbidden, rejection.Kind);
        Assert.Null(confirmation.Applied);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task A_Turn_that_already_executed_a_proposal_is_answered_from_the_ledger_even_though_the_proposal_is_gone()
    {
        var harness = CreateHarness();
        var (token, _, destination) = await ProposeMergeAsync(harness);
        var turnId = TurnId.NewId();

        var first = await ConfirmAsync(harness, token, turnId: turnId);
        Assert.Equal(InventoryConfirmationResultKind.Completed, first.Kind);

        // The same Turn, re-driven after a crash between the mutation and its Outcome. The proposal
        // has been consumed, so only the ledger can answer - and it must, rather than re-executing.
        var replay = await ConfirmAsync(harness, token, turnId: turnId);

        Assert.Equal(InventoryConfirmationResultKind.Completed, replay.Kind);
        Assert.Equal(
            first.Applied!.Changes.Select(change => change.SurvivingStockEntryId),
            replay.Applied!.Changes.Select(change => change.SurvivingStockEntryId));
        Assert.Single(harness.ChangeSetStore.AuditFacts);
        Assert.Equal("14", harness.StockStore.Find(SomeInventory, destination.Id)!.Quantity.ToInvariantText());
    }
    private sealed record ReferenceHarness(
        InventoryConfirmationService Confirmations,
        InMemoryConfirmationProposalStore ProposalStore,
        InMemoryReferenceAdministrationStore AdministrationStore,
        InMemoryInventoryAuthorizationAuditStore AuditStore);

    /// <summary>
    /// The same shape as <see cref="CreateHarness"/>, plus an Owner and the reference administration
    /// store. It deliberately reuses the shipped identities and instant so a reference case and a
    /// stock case are directly comparable.
    /// </summary>
    private static ReferenceHarness CreateReferenceHarness(MembershipRole role)
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Owner, role, Now);

        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);

        var stockStore = new InMemoryStockStore();
        var proposalStore = new InMemoryConfirmationProposalStore();
        var changeSetStore = new InMemoryStockChangeSetStore(stockStore, proposalStore);
        var administrationStore = new InMemoryReferenceAdministrationStore(proposalStore);

        return new ReferenceHarness(
            new InventoryConfirmationService(proposalStore, changeSetStore, administrationStore, authorizationService),
            proposalStore,
            administrationStore,
            auditStore);
    }

    /// <summary>Stores one pending Owner-only Retire proposal and returns it, its plaintext token, and the Unit it would retire.</summary>
    private static async Task<(ConfirmationProposal Proposal, string Token, Guid UnitId)> StoreRetireProposalAsync(
        ReferenceHarness harness)
    {
        var token = ConfirmationToken.Issue();
        var unitId = Guid.NewGuid();

        var proposal = ConfirmationProposal.CreateForReferences(
            ConfirmationToken.HashOf(token),
            Owner,
            Conversation,
            SomeInventory,
            new TurnId(Guid.NewGuid()),
            [
                new ProposedReferenceChange
                {
                    Order = 1,
                    Kind = ReferenceChangeKind.RetireUnit,
                    Target = new ProposedReferenceState(
                        ReferenceKind.Unit, unitId, "Cardboard Box", "cardboard box", Reserved: false),
                },
            ],
            [new ExpectedReferenceVersion(ReferenceKind.Unit, unitId, Guid.NewGuid())],
            [],
            Now);

        await harness.ProposalStore.StoreAsync(proposal, Now, CancellationToken.None);

        return (proposal, token, unitId);
    }

    private static Task<InventoryConfirmationResult> ConfirmAsync(
        ReferenceHarness harness, TurnId turnId, string? token, DirectConfirmationEvidence evidence) =>
        harness.Confirmations.ConfirmAsync(
            Owner, SomeInventory, turnId, token, evidence, Conversation, Now, CancellationToken.None);

    [Fact]
    public async Task An_Owner_confirming_a_Retire_executes_it_exactly_once()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (proposal, token, unitId) = await StoreRetireProposalAsync(harness);
        var turnId = new TurnId(Guid.NewGuid());

        var result = await ConfirmAsync(harness, turnId, token, DirectConfirmationEvidence.Confirmed);

        Assert.Equal(InventoryConfirmationResultKind.Completed, result.Kind);
        var change = Assert.Single(result.AppliedReferences!.Changes);
        Assert.Equal("retire_unit", change.Operation);
        Assert.Equal(unitId.ToString(), change.ReferenceId);
        Assert.Equal("Unit:Retired", Assert.Single(harness.AdministrationStore.Audits).OutcomeCode);
        Assert.Equal(
            ProposalStatus.Confirmed,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_Editor_may_not_confirm_a_Retire_and_the_denial_is_audited()
    {
        var harness = CreateReferenceHarness(MembershipRole.Editor);
        var (proposal, token, _) = await StoreRetireProposalAsync(harness);

        var result = await ConfirmAsync(harness, new TurnId(Guid.NewGuid()), token, DirectConfirmationEvidence.Confirmed);

        Assert.Equal(InventoryConfirmationResultKind.Forbidden, result.Kind);
        Assert.Equal("forbidden", result.Code);
        Assert.Empty(harness.AdministrationStore.Audits);
        Assert.Contains(harness.AuditStore.RecordedFacts, fact => fact.OutcomeCode == "Denied:InsufficientRole");

        // A denied confirmation must not burn the Participant's own pending work: lookup is
        // per-Participant, nobody else can reach it, and it expires on its own in ten minutes.
        Assert.Equal(
            ProposalStatus.Pending,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_reference_proposal_still_needs_the_Participants_own_direct_confirmation()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (proposal, token, _) = await StoreRetireProposalAsync(harness);

        var result = await ConfirmAsync(harness, new TurnId(Guid.NewGuid()), token, DirectConfirmationEvidence.None);

        Assert.Equal(InventoryConfirmationResultKind.Invalid, result.Kind);
        Assert.Equal("confirmation_evidence_missing", result.Code);
        Assert.Empty(harness.AdministrationStore.Audits);
        Assert.Equal(
            ProposalStatus.Pending,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_reference_proposal_whose_reference_moved_underneath_it_conflicts_and_is_settled()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (proposal, token, unitId) = await StoreRetireProposalAsync(harness);
        harness.AdministrationStore.SetVersion(ReferenceKind.Unit, unitId, Guid.NewGuid());

        var result = await ConfirmAsync(harness, new TurnId(Guid.NewGuid()), token, DirectConfirmationEvidence.Confirmed);

        Assert.Equal(InventoryConfirmationResultKind.Conflict, result.Kind);
        Assert.Equal("state_changed", result.Code);
        Assert.Equal(
            ProposalStatus.Conflicted,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_confirmed_Retire_that_stock_now_references_changes_nothing()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (proposal, token, unitId) = await StoreRetireProposalAsync(harness);

        // Stock was created against this Unit after the proposal was reviewed. Current state at
        // execution is what decides, so the confirmation must fail rather than retire a used Unit.
        harness.AdministrationStore.SetStockReferences(ReferenceKind.Unit, unitId, 1);

        var result = await ConfirmAsync(harness, new TurnId(Guid.NewGuid()), token, DirectConfirmationEvidence.Confirmed);

        Assert.Equal(InventoryConfirmationResultKind.Conflict, result.Kind);
        Assert.Empty(harness.AdministrationStore.Audits);
        Assert.Equal(
            ProposalStatus.Conflicted,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_Turn_that_already_executed_a_reference_proposal_re_reports_it()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (_, token, _) = await StoreRetireProposalAsync(harness);
        var turnId = new TurnId(Guid.NewGuid());

        var first = await ConfirmAsync(harness, turnId, token, DirectConfirmationEvidence.Confirmed);
        var replay = await ConfirmAsync(harness, turnId, token, DirectConfirmationEvidence.Confirmed);

        Assert.Equal(InventoryConfirmationResultKind.Completed, first.Kind);
        Assert.Equal(InventoryConfirmationResultKind.Completed, replay.Kind);
        Assert.Equal(
            first.AppliedReferences!.Changes[0].ReferenceId,
            replay.AppliedReferences!.Changes[0].ReferenceId);
        Assert.Single(harness.AdministrationStore.Audits);
    }

    [Fact]
    public async Task Rejecting_a_reference_proposal_changes_nothing_at_all()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (proposal, token, _) = await StoreRetireProposalAsync(harness);

        var result = await harness.Confirmations.RejectAsync(
            Owner, SomeInventory, new TurnId(Guid.NewGuid()), token, DirectConfirmationEvidence.Rejected,
            Conversation, Now, CancellationToken.None);

        Assert.Equal(InventoryConfirmationResultKind.Rejected, result.Kind);
        Assert.Empty(harness.AdministrationStore.Audits);
        Assert.Equal(
            ProposalStatus.Rejected,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }
}
