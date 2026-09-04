using System.Text;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InitialImportServiceTests
{
    private const string Header = "Name,Quantity,Unit,Location,Note";
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly ParticipantId _participantId = new(Guid.NewGuid());
    private readonly UnitId _eachId = new(Guid.NewGuid());
    private readonly LocationId _shelfId = new(Guid.NewGuid());

    private readonly InMemoryInventoryStore _inventories = new(_ => "Participant");
    private readonly InMemoryInventoryAuthorizationAuditStore _audits = new();
    private readonly InMemoryInventoryReferenceStore _references = new();
    private readonly InMemoryReferenceCatalogStore _catalog = new();
    private readonly InMemoryImportProposalStore _proposals = new();
    private readonly InMemoryStockEmptyStateReader _emptyState = new();

    public InitialImportServiceTests()
    {
        _references.AddUnit(_inventoryId, _eachId, "each", "piece", "pieces", "pc", "pcs");
        _references.AddLocation(_inventoryId, _shelfId, "Shelf A");
    }

    private InitialImportService Service() => new(
        new InventoryAuthorizationService(_inventories, _audits),
        _emptyState,
        new ImportReferenceResolver(_references, _catalog),
        _proposals);

    private void GrantMembership(MembershipRole role) =>
        _inventories.GrantMembership(_inventoryId, _participantId, role, Now);

    private Task<ImportValidationResult> ValidateAsync(string csv) =>
        Service().ValidateAsync(_participantId, _inventoryId, Encoding.UTF8.GetBytes(csv), Now, CancellationToken.None);

    private Task<ImportEligibilityResult> EligibilityAsync() =>
        Service().ReadEligibilityAsync(_participantId, _inventoryId, Now, CancellationToken.None);

    [Fact]
    public async Task A_non_member_is_told_nothing_that_would_reveal_the_Inventory_exists()
    {
        var result = await ValidateAsync($"{Header}\nSteel Bolts,4,,,\n");

        Assert.Equal(ImportResultKind.NotFound, result.Kind);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:NotAMember");
    }

    [Fact]
    public async Task A_Viewer_may_not_import_and_the_denial_is_audited()
    {
        GrantMembership(MembershipRole.Viewer);

        var result = await ValidateAsync($"{Header}\nSteel Bolts,4,,,\n");

        Assert.Equal(ImportResultKind.Forbidden, result.Kind);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:InsufficientRole");
    }

    [Theory]
    [InlineData(MembershipRole.Editor)]
    [InlineData(MembershipRole.Owner)]
    public async Task An_Editor_and_an_Owner_may_both_import(MembershipRole role)
    {
        GrantMembership(role);

        var result = await ValidateAsync($"{Header}\nSteel Bolts,4,,,\n");

        Assert.Equal(ImportResultKind.Completed, result.Kind);
    }

    [Fact]
    public async Task Import_is_offered_only_while_the_Inventory_holds_no_Stock_at_all()
    {
        GrantMembership(MembershipRole.Editor);
        Assert.True((await EligibilityAsync()).View!.Eligible);

        // A zero-quantity Stock Entry is still a Stock Entry, which is exactly why this is not filtered.
        _emptyState.SetAnyStock(_inventoryId, true);

        var eligibility = await EligibilityAsync();
        Assert.False(eligibility.View!.Eligible);
        Assert.Equal("inventory_not_empty", eligibility.View.Reason);

        var validation = await ValidateAsync($"{Header}\nSteel Bolts,4,,,\n");
        Assert.Equal(ImportResultKind.NotEmpty, validation.Kind);
    }

    [Fact]
    public async Task A_Viewer_is_not_even_told_whether_the_Inventory_is_empty()
    {
        GrantMembership(MembershipRole.Viewer);

        Assert.Equal(ImportResultKind.Forbidden, (await EligibilityAsync()).Kind);
    }

    [Fact]
    public async Task A_valid_file_previews_the_exact_normalized_entries_it_would_create()
    {
        GrantMembership(MembershipRole.Editor);

        var result = await ValidateAsync(
            $"{Header}\nSteel Bolts,4,,Shelf A,Blue box\nBrass Rivets,2.5,piece,,\nSTEEL bolts,6,,Shelf A,\n");

        Assert.Equal(ImportResultKind.Completed, result.Kind);
        var preview = result.View!;
        Assert.Equal(3, preview.SourceRowCount);
        Assert.Equal(2, preview.Entries.Count);

        var bolts = preview.Entries[0];
        Assert.Equal("Steel Bolts", bolts.Name);
        Assert.Equal("10", bolts.Quantity);
        Assert.Equal("each", bolts.UnitCanonicalName);
        Assert.Equal("Shelf A", bolts.LocationName);
        Assert.Equal("Blue box", bolts.Note);
        Assert.Equal([2, 4], bolts.SourceLineNumbers);

        Assert.Equal("Brass Rivets", preview.Entries[1].Name);
        Assert.Null(preview.Entries[1].LocationName);
    }

    [Fact]
    public async Task A_successful_validation_stores_a_pending_proposal_bound_to_everything_that_decided_it()
    {
        GrantMembership(MembershipRole.Editor);
        var csv = $"{Header}\nSteel Bolts,4,,,\n";

        var result = await ValidateAsync(csv);

        var stored = await _proposals.FindPendingAsync(_participantId, _inventoryId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(_participantId, stored!.ParticipantId);
        Assert.Equal(_inventoryId, stored.InventoryId);
        Assert.Equal(FileDigest.Of(Encoding.UTF8.GetBytes(csv)), stored.FileDigest);
        Assert.Equal(EmptyStateVersion.Empty, stored.EmptyStateVersion);
        Assert.Equal(Now.AddMinutes(ImportProposal.LifetimeMinutes), stored.ExpiresAt);
        Assert.Equal(stored.FileDigest.Value, result.View!.FileDigest);

        // The plaintext token exists only in this answer; the row carries its hash.
        Assert.True(ConfirmationToken.IsWellFormed(result.View.Token));
        Assert.True(ConfirmationToken.Matches(stored.TokenHash, result.View.Token));
    }

    [Fact]
    public async Task The_raw_file_is_kept_for_the_proposal_and_nowhere_else()
    {
        GrantMembership(MembershipRole.Editor);
        var csv = $"{Header}\nSteel Bolts,4,,,\n";

        await ValidateAsync(csv);

        var stored = await _proposals.FindPendingAsync(_participantId, _inventoryId, CancellationToken.None);
        var raw = await _proposals.FindRawContentAsync(stored!.Id, CancellationToken.None);
        Assert.Equal(Encoding.UTF8.GetBytes(csv), raw!.Value.ToArray());
    }

    [Fact]
    public async Task Validating_again_replaces_this_Participants_own_pending_import()
    {
        GrantMembership(MembershipRole.Editor);

        var first = await ValidateAsync($"{Header}\nSteel Bolts,4,,,\n");
        var second = await ValidateAsync($"{Header}\nBrass Rivets,1,,,\n");

        Assert.True(second.View!.SupersededPrevious);
        Assert.NotEqual(first.View!.Token, second.View.Token);

        var pending = await _proposals.FindPendingAsync(_participantId, _inventoryId, CancellationToken.None);
        Assert.Equal("Brass Rivets", Assert.Single(pending!.Entries).Name);
    }

    [Fact]
    public async Task Every_actionable_error_comes_back_together_and_nothing_is_stored()
    {
        GrantMembership(MembershipRole.Editor);

        var result = await ValidateAsync(
            $"{Header}\n,4,,,\nSteel Bolts,nope,,,\nBrass Rivets,1,crate,,\nZinc,1,,Bay 9,\n");

        Assert.Equal(ImportResultKind.Invalid, result.Kind);
        Assert.Equal(
            ["missing_name", "invalid_quantity", "unknown_unit", "unknown_location"],
            result.Errors.Select(error => error.Code));
        Assert.Equal([2, 3, 4, 5], result.Errors.Select(error => error.LineNumber));
        Assert.Null(await _proposals.FindPendingAsync(_participantId, _inventoryId, CancellationToken.None));
    }

    [Fact]
    public async Task Row_errors_are_reported_before_references_are_even_looked_up()
    {
        GrantMembership(MembershipRole.Editor);

        var result = await ValidateAsync($"{Header}\n,4,crate,,\n");

        // One line, one answer: the row is unreadable, so its unknown Unit is not piled on top.
        Assert.Equal("missing_name", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Conflicting_Notes_on_equivalent_rows_are_reported_as_errors()
    {
        GrantMembership(MembershipRole.Editor);

        var result = await ValidateAsync($"{Header}\nSteel Bolts,4,,,Blue box\nSteel Bolts,4,,,Red box\n");

        Assert.Equal("conflicting_notes", Assert.Single(result.Errors).Code);
        Assert.Equal(ImportResultKind.Invalid, result.Kind);
    }

    [Fact]
    public async Task An_unknown_reference_error_carries_its_bounded_suggestions()
    {
        GrantMembership(MembershipRole.Editor);
        _catalog.AddUnit(_inventoryId, "Crate Large", []);

        var result = await ValidateAsync($"{Header}\nSteel Bolts,4,crate,,\n");

        Assert.Equal(["Crate Large"], Assert.Single(result.Errors).Suggestions);
    }

    [Fact]
    public async Task An_answer_never_carries_more_than_the_bounded_number_of_errors_and_says_how_many_it_omitted()
    {
        GrantMembership(MembershipRole.Editor);
        var builder = new StringBuilder(Header).Append('\n');
        for (var row = 0; row < ImportContract.MaxReportedErrors + 25; row++)
        {
            builder.Append(",1,,,\n");
        }

        var result = await ValidateAsync(builder.ToString());

        Assert.Equal(ImportContract.MaxReportedErrors, result.Errors.Count);
        Assert.Equal(25, result.OmittedErrorCount);
    }

    [Fact]
    public async Task A_file_whose_envelope_is_broken_is_reported_without_any_row_noise()
    {
        GrantMembership(MembershipRole.Editor);

        var result = await ValidateAsync("Name,Quantity,Unit,Location,Colour\n,1,,,\n,1,,,\n");

        Assert.Equal("unknown_column", Assert.Single(result.Errors).Code);
    }
}
