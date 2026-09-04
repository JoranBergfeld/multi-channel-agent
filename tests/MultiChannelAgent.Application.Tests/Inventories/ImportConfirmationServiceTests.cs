using System.Text;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ImportConfirmationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly ParticipantId _participantId = new(Guid.NewGuid());
    private readonly UnitId _eachId = new(Guid.NewGuid());

    private readonly InMemoryInventoryStore _inventories = new(_ => "Participant");
    private readonly InMemoryInventoryAuthorizationAuditStore _audits = new();
    private readonly InMemoryImportProposalStore _proposals = new();
    private readonly InMemoryStockEmptyStateReader _emptyState = new();
    private readonly InMemoryImportExecutionStore _execution;

    public ImportConfirmationServiceTests()
    {
        _execution = new InMemoryImportExecutionStore(_proposals, _emptyState);
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Editor, Now);
    }

    private ImportConfirmationService Service() => new(
        new InventoryAuthorizationService(_inventories, _audits), _proposals, _execution);

    private async Task<(ImportProposal Proposal, string Token)> StorePendingAsync(int entryCount = 1)
    {
        var token = ConfirmationToken.Issue();
        var entries = Enumerable.Range(0, entryCount).Select(index => new ImportEntry
        {
            LineNumber = index + 2,
            SourceLineNumbers = [index + 2],
            Name = $"Item {index}",
            NormalizedName = $"item {index}",
            Quantity = Quantity.Create(4m),
            UnitId = _eachId,
            UnitCanonicalName = "each",
            LocationId = null,
            LocationName = null,
            Note = null,
        }).ToList();

        var proposal = ImportProposal.Create(
            ConfirmationToken.HashOf(token),
            _participantId,
            _inventoryId,
            FileDigest.Of(Encoding.UTF8.GetBytes("Name,Quantity,Unit,Location,Note\n")),
            entries,
            EmptyStateVersion.Empty,
            Now);

        await _proposals.StoreAsync(proposal, new byte[] { 1, 2, 3 }, Now, CancellationToken.None);

        return (proposal, token);
    }

    private Task<ImportConfirmationResult> ConfirmAsync(
        ImportProposalId proposalId, string? token, DateTimeOffset? at = null) =>
        Service().ConfirmAsync(_participantId, _inventoryId, proposalId, token, at ?? Now, CancellationToken.None);

    [Fact]
    public async Task Confirming_creates_every_entry_exactly_once_and_audits_one_fact()
    {
        var (proposal, token) = await StorePendingAsync(entryCount: 3);

        var result = await ConfirmAsync(proposal.Id, token);

        Assert.Equal(ImportConfirmationResultKind.Completed, result.Kind);
        Assert.Equal(3, result.View!.CreatedEntryCount);
        Assert.Equal(proposal.FileDigest.Value, result.View.FileDigest);
        Assert.Equal(proposal.Id.ToString(), result.View.ProposalId);
        Assert.Equal(3, _execution.CreatedEntries.Count);
        Assert.Equal("Import:Completed", Assert.Single(_execution.Audits).OutcomeCode);
        Assert.Equal(
            ImportProposalStatus.Confirmed,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Confirmation_applies_the_stored_rows_and_never_reads_the_file()
    {
        var (proposal, token) = await StorePendingAsync();

        await ConfirmAsync(proposal.Id, token);

        Assert.Null(await _proposals.FindRawContentAsync(proposal.Id, CancellationToken.None));
        Assert.Equal("Item 0", Assert.Single(_execution.CreatedEntries).Name);
    }

    [Fact]
    public async Task The_token_is_single_use()
    {
        var (proposal, token) = await StorePendingAsync();

        Assert.Equal(ImportConfirmationResultKind.Completed, (await ConfirmAsync(proposal.Id, token)).Kind);

        var reused = await ConfirmAsync(proposal.Id, token);
        Assert.Equal(ImportConfirmationResultKind.Completed, reused.Kind);
        Assert.Single(_execution.CreatedEntries);
        Assert.Single(_execution.Audits);
    }

    [Fact]
    public async Task A_wrong_token_leaves_the_proposal_pending_so_a_typo_destroys_nothing()
    {
        var (proposal, _) = await StorePendingAsync();

        var result = await ConfirmAsync(proposal.Id, ConfirmationToken.Issue());

        Assert.Equal(ImportConfirmationResultKind.Invalid, result.Kind);
        Assert.Equal("proposal_token_mismatch", result.Code);
        Assert.Equal(
            ImportProposalStatus.Pending,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Empty(_execution.CreatedEntries);
    }

    [Fact]
    public async Task An_expired_proposal_is_settled_and_creates_nothing()
    {
        var (proposal, token) = await StorePendingAsync();

        var result = await ConfirmAsync(proposal.Id, token, Now.AddMinutes(ImportProposal.LifetimeMinutes));

        Assert.Equal(ImportConfirmationResultKind.Conflict, result.Kind);
        Assert.Equal("proposal_expired", result.Code);
        Assert.Equal(
            ImportProposalStatus.Expired,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Empty(_execution.CreatedEntries);
    }

    [Fact]
    public async Task Confirming_into_an_Inventory_that_is_no_longer_empty_creates_nothing()
    {
        var (proposal, token) = await StorePendingAsync();
        _emptyState.SetAnyStock(_inventoryId, true);

        var result = await ConfirmAsync(proposal.Id, token);

        Assert.Equal(ImportConfirmationResultKind.Conflict, result.Kind);
        Assert.Equal("state_changed", result.Code);
        Assert.Empty(_execution.CreatedEntries);
        Assert.Equal(
            ImportProposalStatus.Conflicted,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_replayed_confirmation_re_reports_what_it_did_instead_of_importing_twice()
    {
        var (proposal, token) = await StorePendingAsync(entryCount: 2);
        await ConfirmAsync(proposal.Id, token);

        var replay = await Service().ReplayAsync(
            _participantId, _inventoryId, proposal.Id, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.Completed, replay.Kind);
        Assert.Equal(2, replay.View!.CreatedEntryCount);
        Assert.Equal(2, _execution.CreatedEntries.Count);
        Assert.Single(_execution.Audits);
    }

    [Fact]
    public async Task Rejecting_settles_the_proposal_discards_the_file_and_creates_nothing()
    {
        var (proposal, token) = await StorePendingAsync();

        var result = await Service().RejectAsync(
            _participantId, _inventoryId, proposal.Id, token, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.Rejected, result.Kind);
        Assert.Equal(
            ImportProposalStatus.Rejected,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await _proposals.FindRawContentAsync(proposal.Id, CancellationToken.None));
        Assert.Empty(_execution.CreatedEntries);
    }

    [Fact]
    public async Task Rejecting_needs_no_token_because_declining_is_always_safe()
    {
        var (proposal, _) = await StorePendingAsync();

        var result = await Service().RejectAsync(
            _participantId, _inventoryId, proposal.Id, null, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.Rejected, result.Kind);
        Assert.Equal(
            ImportProposalStatus.Rejected,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Another_Participants_pending_import_is_unreachable()
    {
        var (proposal, token) = await StorePendingAsync();
        var stranger = new ParticipantId(Guid.NewGuid());
        _inventories.GrantMembership(_inventoryId, stranger, MembershipRole.Editor, Now);

        var result = await Service().ConfirmAsync(
            stranger, _inventoryId, proposal.Id, token, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.NotFound, result.Kind);
        Assert.Empty(_execution.CreatedEntries);
    }

    [Fact]
    public async Task A_Viewer_may_not_confirm_and_the_denial_is_audited()
    {
        var (proposal, token) = await StorePendingAsync();
        var viewer = new ParticipantId(Guid.NewGuid());
        _inventories.GrantMembership(_inventoryId, viewer, MembershipRole.Viewer, Now);

        var result = await Service().ConfirmAsync(
            viewer, _inventoryId, proposal.Id, token, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.Forbidden, result.Kind);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:InsufficientRole");
        Assert.Empty(_execution.CreatedEntries);
    }

    [Fact]
    public async Task Replay_does_not_disclose_another_Participants_import()
    {
        var (proposal, token) = await StorePendingAsync();
        await ConfirmAsync(proposal.Id, token);
        var otherEditor = new ParticipantId(Guid.NewGuid());
        _inventories.GrantMembership(_inventoryId, otherEditor, MembershipRole.Editor, Now);

        var result = await Service().ReplayAsync(
            otherEditor, _inventoryId, proposal.Id, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task A_stale_rejection_cannot_settle_the_newer_pending_import()
    {
        var (first, _) = await StorePendingAsync();
        var (replacement, _) = await StorePendingAsync();

        var result = await Service().RejectAsync(
            _participantId, _inventoryId, first.Id, null, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.NotFound, result.Kind);
        Assert.Equal(
            ImportProposalStatus.Pending,
            await _proposals.FindStatusAsync(replacement.Id, CancellationToken.None));
    }
}
