import { useCallback, useEffect, useState } from 'react';
import {
  createInventory,
  fetchBootstrap,
  selectInventory,
  MAX_INVENTORY_NAME_LENGTH,
  type BootstrapResponse,
  type InventoryView,
} from './sessionApi';
import TurnTracer from './TurnTracer';

type SessionState =
  | { phase: 'loading' }
  | { phase: 'unauthenticated' }
  | { phase: 'forbidden' }
  | { phase: 'ready'; session: BootstrapResponse };

/**
 * Signed-in web entry point: resolves the authenticated session bootstrap, guides a Participant
 * with no Memberships through onboarding, and otherwise lets them explicitly create and select
 * among their authorized Inventories before reaching the (preserved) Turn tracer.
 */
function App() {
  const [state, setState] = useState<SessionState>({ phase: 'loading' });
  const [error, setError] = useState<string | null>(null);
  const [newInventoryName, setNewInventoryName] = useState('');
  const [creating, setCreating] = useState(false);
  const [selectingId, setSelectingId] = useState<string | null>(null);

  const loadSession = useCallback(async () => {
    try {
      const result = await fetchBootstrap();
      if (result.status === 'ok') {
        setState({ phase: 'ready', session: result.data });
      } else {
        setState({ phase: result.status });
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, []);

  useEffect(() => {
    // oxlint(react/set-state-in-effect) only recognizes an inline async IIFE's await boundary, not
    // one behind a named function reference - even though every setState call inside loadSession
    // already happens after its own internal await, never synchronously during this effect. Wrapping
    // the call this way keeps loadSession reusable (retries, and the post-create/post-select
    // refreshes below) while making that already-true post-await ordering visible to the linter too.
    void (async () => {
      await loadSession();
    })();
  }, [loadSession]);

  async function handleCreateInventory(event: React.FormEvent) {
    event.preventDefault();
    if (state.phase !== 'ready') {
      return;
    }

    setCreating(true);
    setError(null);

    try {
      await createInventory(newInventoryName, crypto.randomUUID(), state.session.csrfToken);
      setNewInventoryName('');
      await loadSession();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setCreating(false);
    }
  }

  async function handleSelectInventory(inventory: InventoryView) {
    if (state.phase !== 'ready') {
      return;
    }

    setSelectingId(inventory.id);
    setError(null);

    try {
      const authorized = await selectInventory(inventory.id, state.session.csrfToken);
      if (!authorized) {
        setError('That Inventory is not available.');
        return;
      }

      await loadSession();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSelectingId(null);
    }
  }

  if (state.phase === 'loading') {
    return (
      <main>
        <h1>Multi-Channel Agent</h1>
        <p>Loading your session…</p>
      </main>
    );
  }

  if (state.phase === 'unauthenticated') {
    return (
      <main>
        <h1>Multi-Channel Agent</h1>
        <p>Sign in with your organization account to continue.</p>
        <a href="/auth/sign-in">Sign in</a>
      </main>
    );
  }

  if (state.phase === 'forbidden') {
    return (
      <main>
        <h1>Multi-Channel Agent</h1>
        <p role="alert">Your account cannot use this application right now.</p>
      </main>
    );
  }

  const { session } = state;
  const { bootstrap } = session;

  return (
    <main>
      <h1>Multi-Channel Agent</h1>
      <p>
        Signed in as <strong>{bootstrap.displayName}</strong>
      </p>

      {error && <p role="alert">{error}</p>}

      {bootstrap.needsOnboarding && (
        <section>
          <h2>Get started</h2>
          <p>You don&apos;t belong to any Inventory yet. Create one to get started.</p>
        </section>
      )}

      <section>
        <h2>Your Inventories</h2>
        {bootstrap.inventories.length === 0 && !bootstrap.needsOnboarding && <p>No Inventories yet.</p>}
        {bootstrap.inventories.length > 0 && (
          <ul>
            {bootstrap.inventories.map((inventory) => {
              const isActive = inventory.id === bootstrap.activeInventoryId;
              return (
                <li key={inventory.id}>
                  {inventory.name} — Owner: {inventory.ownerDisplayName} (#{inventory.shortId}) — {inventory.role}
                  {isActive ? (
                    <strong> (active)</strong>
                  ) : (
                    <button
                      type="button"
                      onClick={() => void handleSelectInventory(inventory)}
                      disabled={selectingId === inventory.id}
                    >
                      {selectingId === inventory.id ? 'Selecting…' : 'Use in this conversation'}
                    </button>
                  )}
                </li>
              );
            })}
          </ul>
        )}

        <form onSubmit={handleCreateInventory}>
          <label htmlFor="newInventoryName">New Inventory name</label>
          <input
            id="newInventoryName"
            value={newInventoryName}
            onChange={(event) => setNewInventoryName(event.target.value)}
            maxLength={MAX_INVENTORY_NAME_LENGTH}
            required
          />
          <button type="submit" disabled={creating || newInventoryName.trim().length === 0}>
            {creating ? 'Creating…' : 'Create Inventory'}
          </button>
        </form>
      </section>

      <TurnTracer />
    </main>
  );
}

export default App;
