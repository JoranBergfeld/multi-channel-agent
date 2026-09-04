using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>The semantic shape of an Initial Import answer. Only these; nothing here invents a status.</summary>
public enum ImportResultKind
{
    Completed,

    /// <summary>No accessible Inventory. Deliberately identical whether it does not exist or is not this Participant's.</summary>
    NotFound,

    /// <summary>A member, but not an Editor or Owner.</summary>
    Forbidden,

    /// <summary>The Inventory already holds Stock, so there is nothing initial to import.</summary>
    NotEmpty,

    /// <summary>The file could not be understood. <see cref="ImportValidationResult.Errors"/> says why, for every line at once.</summary>
    Invalid,
}

/// <summary>Whether Initial Import is available, and when it is not, the one machine code saying why.</summary>
public sealed record ImportEligibilityView(bool Eligible, string? Reason);

public sealed record ImportEligibilityResult(ImportResultKind Kind, ImportEligibilityView? View);

/// <summary>One entry the import would create, exactly as it will be created.</summary>
public sealed record ImportPreviewRowView(
    string Name,
    string Quantity,
    string UnitCanonicalName,
    string? LocationName,
    string? Note,
    IReadOnlyList<int> SourceLineNumbers);

/// <summary>
/// The exact normalized preview, plus the one-time token that confirms it. The token is the only
/// place the plaintext ever exists; the stored proposal keeps its hash.
/// </summary>
public sealed record ImportPreviewView(
    string Token,
    string FileDigest,
    int SourceRowCount,
    IReadOnlyList<ImportPreviewRowView> Entries,
    bool SupersededPrevious,
    DateTimeOffset ExpiresAt);

/// <summary>One reported problem: its machine code, where it is, and any bounded suggestions.</summary>
public sealed record ImportErrorView(string Code, int LineNumber, int? ColumnIndex, IReadOnlyList<string> Suggestions);

/// <summary>
/// The answer to a validation. Exactly one of <see cref="View"/> and <see cref="Errors"/> carries
/// anything: a file either previews cleanly or is reported, never both.
/// </summary>
public sealed record ImportValidationResult(
    ImportResultKind Kind,
    ImportPreviewView? View,
    IReadOnlyList<ImportErrorView> Errors,
    int OmittedErrorCount)
{
    public static ImportValidationResult Refused(ImportResultKind kind) => new(kind, null, [], 0);
}

/// <summary>
/// Initial Import's eligibility and validation half: authorize, gate on the empty Inventory, read the
/// file, resolve its references, merge its equivalent rows, and either report everything that is
/// wrong or store the exact proposal a confirmation will apply.
///
/// Nothing is written until every row has passed - #26 says "Validate the whole file and report all
/// actionable row/column errors. Never partially import." - and the phases are ordered so a
/// Participant is never told about an unknown Unit on a line whose Name is missing, because the line
/// they need to fix is the same line either way.
///
/// A reference error on one row never withholds the merge from every other row: the merge always runs
/// over whatever resolved, because a row's own resolution never depended on any other row's, and a
/// Participant fixing an unknown Unit on line 4 deserves to hear about a Notes conflict on lines 9 and
/// 12 in the same answer rather than on a second upload. Nothing is ever stored, though, while any
/// error - row, reference, or merge - remains; "report everything, store nothing but a clean file" is
/// one rule, not two.
/// </summary>
public sealed class InitialImportService(
    InventoryAuthorizationService authorizationService,
    IStockEmptyStateReader emptyStateReader,
    ImportReferenceResolver resolver,
    IImportProposalStore proposalStore)
{
    private const int MaxSuggestionEnrichedErrors = 10;

    public async Task<ImportEligibilityResult> ReadEligibilityAsync(
        ParticipantId participantId, InventoryId inventoryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId: null, now, cancellationToken);

        if (RefusalFor(authorization.Outcome) is { } refusal)
        {
            return new ImportEligibilityResult(refusal, null);
        }

        return await emptyStateReader.AnyStockAsync(inventoryId, cancellationToken)
            ? new ImportEligibilityResult(ImportResultKind.Completed, new ImportEligibilityView(false, "inventory_not_empty"))
            : new ImportEligibilityResult(ImportResultKind.Completed, new ImportEligibilityView(true, null));
    }

    public async Task<ImportValidationResult> ValidateAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        ReadOnlyMemory<byte> content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId: null, now, cancellationToken);

        if (RefusalFor(authorization.Outcome) is { } refusal)
        {
            return ImportValidationResult.Refused(refusal);
        }

        // Asked before the file is even read: importing into an Inventory that already holds Stock is
        // not a validation failure, it is a workflow that does not apply.
        if (await emptyStateReader.AnyStockAsync(inventoryId, cancellationToken))
        {
            return ImportValidationResult.Refused(ImportResultKind.NotEmpty);
        }

        var digest = FileDigest.Of(content.Span);

        // Phase 1: the envelope. A file whose headers or quoting are wrong produces no row errors at
        // all, because rows read against a broken envelope would be noise rather than help.
        var read = CsvImportDocument.Read(content.Span);
        if (read.Document is null)
        {
            return Invalid(read.Errors.Select(Plain));
        }

        // Phase 2: the rows, on their own terms.
        var rows = new List<ImportRow>(read.Document.Records.Count);
        var rowErrors = new List<ImportRowError>(read.Errors);

        foreach (var record in read.Document.Records)
        {
            if (ImportRow.TryCreate(record, out var row, out var errors))
            {
                rows.Add(row!);
            }
            else
            {
                rowErrors.AddRange(errors);
            }
        }

        // Phase 3: this Inventory's references, resolved only for the rows that parsed - a row with
        // its own error was never readable enough to look anything up for, so it contributes nothing
        // here and its Unit or Location is never even queried. A row's resolution never depends on any
        // other row's, so a reference error on one row is never a reason to withhold a resolved row
        // from Phase 4 - only its own line is unusable, not the file.
        // Every unknown remains an exact error, but only the first few distinct terms are enriched
        // with catalog suggestions. The SQL catalog may need two ordered reads per term, so tying
        // enrichment to the 500-error response cap would permit roughly 1,000 sequential round trips
        // for one invalid upload. Ten enriched errors are enough to expose the correction pattern
        // without making an already-invalid file expensive to diagnose.
        var resolution = await resolver.ResolveAsync(inventoryId, rows, MaxSuggestionEnrichedErrors, cancellationToken);

        // Phase 4: equivalence and Notes, over whatever resolved. This runs even when Phase 2 or
        // Phase 3 already found errors elsewhere, because a merge error here (a Notes conflict, a
        // Quantity overflow) is just as independent of those as they are of each other - a Participant
        // fixing an unknown Unit on one line deserves to hear about a Notes conflict on two entirely
        // different lines in the same answer.
        var merged = ImportMergePlan.Create(resolution.Rows);

        if (rowErrors.Count > 0 || resolution.Errors.Count > 0 || merged.Errors.Count > 0)
        {
            return Invalid(Combined(rowErrors, resolution.Errors, merged.Errors));
        }

        var token = ConfirmationToken.Issue();
        var proposal = ImportProposal.Create(
            ConfirmationToken.HashOf(token),
            participantId,
            inventoryId,
            digest,
            merged.Entries,
            EmptyStateVersion.Empty,
            now);

        var superseded = await proposalStore.StoreAsync(proposal, content, now, cancellationToken);

        return new ImportValidationResult(
            ImportResultKind.Completed,
            new ImportPreviewView(
                token,
                digest.Value,
                read.Document.Records.Count,
                [.. merged.Entries.Select(ToPreviewRow)],
                superseded,
                proposal.ExpiresAt),
            [],
            0);
    }

    private static ImportPreviewRowView ToPreviewRow(ImportEntry entry) => new(
        entry.Name,
        entry.Quantity.ToInvariantText(),
        entry.UnitCanonicalName,
        entry.LocationName,
        entry.Note,
        entry.SourceLineNumbers);

    /// <summary>
    /// Row errors, reference errors, and merge errors together, in source order. All three phases run
    /// over disjoint rows - a row's own error means its Unit and Location were never looked up, and a
    /// merge error only ever names a row that did resolve - so nothing here is ever reported twice for
    /// the same line, however many of the three independently found something wrong.
    /// </summary>
    private static IEnumerable<ImportErrorView> Combined(
        IEnumerable<ImportRowError> rowErrors,
        IEnumerable<ImportReferenceError> referenceErrors,
        IEnumerable<ImportRowError> mergeErrors)
    {
        var views = rowErrors.Select(Plain)
            .Concat(referenceErrors.Select(error => new ImportErrorView(
                ImportFacts.ToMachineText(error.Code), error.LineNumber, error.ColumnIndex, error.Suggestions)))
            .Concat(mergeErrors.Select(Plain));

        return views.OrderBy(view => view.LineNumber).ThenBy(view => view.ColumnIndex ?? -1);
    }

    private static ImportErrorView Plain(ImportRowError error) =>
        new(ImportFacts.ToMachineText(error.Code), error.LineNumber, error.ColumnIndex, []);

    /// <summary>
    /// Bounds the report at <see cref="ImportContract.MaxReportedErrors"/> and states exactly how many
    /// were omitted. The promise is that a Participant can fix the file once, not that every one of
    /// five thousand broken rows is enumerated - and an exact count is what keeps that honest.
    /// </summary>
    private static ImportValidationResult Invalid(IEnumerable<ImportErrorView> errors)
    {
        var all = errors.ToList();
        var reported = all.Take(ImportContract.MaxReportedErrors).ToList();

        return new ImportValidationResult(ImportResultKind.Invalid, null, reported, all.Count - reported.Count);
    }

    private static ImportResultKind? RefusalFor(InventoryAuthorizationOutcome outcome) => outcome switch
    {
        InventoryAuthorizationOutcome.NotFound => ImportResultKind.NotFound,
        InventoryAuthorizationOutcome.Forbidden => ImportResultKind.Forbidden,
        _ => null,
    };
}
