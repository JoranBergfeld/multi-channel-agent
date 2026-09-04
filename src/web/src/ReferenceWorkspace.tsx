import { useCallback, useEffect, useState } from 'react';
import { fetchLocations, fetchUnits, type LocationListView, type UnitListView } from './referenceApi';

interface ReferenceWorkspaceProps {
  inventoryId: string;
  /** Bumped by the parent whenever a terminal Outcome arrives, to trigger a refetch. */
  refetchToken: number;
}

/**
 * The Inventory workspace's authoritative reference projection: the active Units (with their
 * aliases) and the active Locations, refetched whenever the parent signals a terminal Outcome
 * arrived. Retired references are excluded server-side, so a Unit or Location that has just been
 * retired conversationally stops being offered here in the same breath.
 */
function ReferenceWorkspace({ inventoryId, refetchToken }: ReferenceWorkspaceProps) {
  const [units, setUnits] = useState<UnitListView | null>(null);
  const [locations, setLocations] = useState<LocationListView | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setUnits(await fetchUnits(inventoryId));
      setLocations(await fetchLocations(inventoryId));

      // A refetch that succeeded is the authoritative view, so an earlier failure must stop being
      // shown: leaving it would keep the workspace stuck on a stale error for the rest of the
      // session, hiding the very catalog it just loaded.
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [inventoryId]);

  useEffect(() => {
    // oxlint(react/set-state-in-effect) only recognizes an inline async IIFE's await boundary, not
    // one behind a named function reference - even though load's setState calls already happen after
    // its own internal awaits. See StockWorkspace.tsx for the same pattern.
    void (async () => {
      await load();
    })();
    // refetchToken deliberately participates in this effect's dependency list purely to trigger a
    // refetch when it changes - its value itself is never read.
  }, [load, refetchToken]);

  if (error) {
    return (
      <section role="alert">
        <h2>Units and Locations</h2>
        <p>{error}</p>
      </section>
    );
  }

  return (
    <section>
      <h2>Units and Locations</h2>
      <h3>Units</h3>
      {!units || units.units.length === 0 ? (
        <p>No Units yet.</p>
      ) : (
        <ul>
          {units.units.map((unit) => (
            <li key={unit.id}>
              {unit.name}
              {unit.aliases.length > 0 && ` (${unit.aliases.join(', ')})`}
            </li>
          ))}
        </ul>
      )}
      <h3>Locations</h3>
      {!locations || locations.locations.length === 0 ? (
        <p>No Locations yet. Stock with no Location is unlocated.</p>
      ) : (
        <ul>
          {locations.locations.map((location) => (
            <li key={location.id}>{location.name}</li>
          ))}
        </ul>
      )}
    </section>
  );
}

export default ReferenceWorkspace;
