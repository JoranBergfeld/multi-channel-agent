namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// Which Inventory-owned references a set of stock changes depends on.
///
/// Two callers need exactly this answer and must not disagree. A stored
/// <see cref="ConfirmationProposal"/> records it so retiring a reference can settle every pending
/// proposal that depended on it, and the change-set writer asks for it so it can hold those very
/// references while it writes. A reference one of them accounted for and the other did not is a
/// reference that gets retired underneath a change nobody re-checked - so the derivation lives here,
/// once, rather than in each of them.
///
/// Both answers are de-duplicated and keep the order a reference was first seen in: changes in the
/// order given, source before destination, then the expected absences. Order is not a correctness
/// property of the set itself, but a caller taking locks needs a deterministic one, and a caller
/// reporting them benefits from a stable one.
/// </summary>
public static class StockReferenceDependencies
{
    /// <summary>Every Unit these changes and expected absences reference.</summary>
    public static IReadOnlyList<UnitId> UnitsOf(
        IReadOnlyList<ProposedChange> changes, IReadOnlyList<ExpectedEquivalentStockAbsence> absences)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(absences);

        return
        [
            .. changes
                .SelectMany(change => new[] { (UnitId?)change.Source.UnitId, change.Destination?.UnitId })
                .Concat(absences.Select(absence => (UnitId?)absence.UnitId))
                .OfType<UnitId>()
                .Distinct(),
        ];
    }

    /// <summary>
    /// Every Location these changes and expected absences reference. Unlocated stock is the absence of
    /// a Location rather than a Location of its own, so it contributes nothing here - which is exactly
    /// why "unlocated" can never be retired.
    /// </summary>
    public static IReadOnlyList<LocationId> LocationsOf(
        IReadOnlyList<ProposedChange> changes, IReadOnlyList<ExpectedEquivalentStockAbsence> absences)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(absences);

        return
        [
            .. changes
                .SelectMany(change => new[] { change.Source.LocationId, change.Destination?.LocationId })
                .Concat(absences.Select(absence => absence.LocationId))
                .OfType<LocationId>()
                .Distinct(),
        ];
    }
}
