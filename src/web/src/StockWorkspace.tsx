import { useCallback, useEffect, useState } from 'react';
import { fetchStock, type StockListView } from './stockApi';

interface StockWorkspaceProps {
  inventoryId: string;
  /** Bumped by the parent whenever a terminal read Outcome arrives, to trigger a refetch. */
  refetchToken: number;
}

/**
 * The Inventory workspace's authoritative Stock projection: the same authorized read the
 * conversational list_stock tool call uses, refetched whenever the parent signals a terminal read
 * Outcome arrived (see <c>refetchToken</c>) so the workspace never shows stale results after a chat
 * command changes what should be visible.
 */
function StockWorkspace({ inventoryId, refetchToken }: StockWorkspaceProps) {
  const [view, setView] = useState<StockListView | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setView(await fetchStock(inventoryId));

      // A refetch that succeeded is the authoritative view, so an earlier failure must stop being
      // shown: leaving it would keep the workspace stuck on a stale error message for the rest of
      // the session, hiding the very Stock it just loaded.
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [inventoryId]);

  useEffect(() => {
    // oxlint(react/set-state-in-effect) only recognizes an inline async IIFE's await boundary, not
    // one behind a named function reference - even though load's setState call already happens
    // after its own internal await. Wrapping the call this way keeps load reusable while making that
    // already-true post-await ordering visible to the linter too (see App.tsx for the same pattern).
    void (async () => {
      await load();
    })();
    // refetchToken deliberately participates in this effect's dependency list purely to trigger a
    // refetch when it changes - its value itself is never read.
  }, [load, refetchToken]);

  if (error) {
    return (
      <section role="alert">
        <h2>Stock</h2>
        <p>{error}</p>
      </section>
    );
  }

  return (
    <section>
      <h2>Stock</h2>
      {!view || view.rows.length === 0 ? (
        <p>No on-hand Stock Entries yet.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Unit</th>
              <th>Location</th>
              <th>Quantity</th>
            </tr>
          </thead>
          <tbody>
            {view.rows.map((row) => (
              <tr key={row.id}>
                <td>{row.name}</td>
                <td>{row.unit}</td>
                <td>{row.location ?? '—'}</td>
                <td>{row.quantity}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

export default StockWorkspace;
