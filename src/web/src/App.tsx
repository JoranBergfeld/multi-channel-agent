import { useCallback, useEffect, useRef, useState } from 'react';
import {
  createInventory,
  fetchBootstrap,
  selectInventory,
  MAX_INVENTORY_NAME_LENGTH,
  type BootstrapResponse,
  type BootstrapResult,
  type InventoryView,
} from './sessionApi';
import { clearInFlightTurn } from './conversationStorage';
import { startNewConversation } from './conversationApi';
import { openInventoryStream, type InventoryVersions } from './inventoryStream';
import { releaseVoice } from './voiceApi';
import { type TurnSubmissionInput } from './useTurnSubmission';
import { BrowserVoiceTransport } from './browserVoiceTransport';
import type { VoiceTransport } from './voiceTransport';
import type { FinalizedUtterance } from './voiceReducer';
import InitialImport from './InitialImport';
import InventoryGovernance from './InventoryGovernance';
import ReferenceWorkspace from './ReferenceWorkspace';
import StockWorkspace from './StockWorkspace';
import TurnTracer from './TurnTracer';
import VoiceControls from './VoiceControls';
import WorkspacePanel from './WorkspacePanel';

type SessionState =
  | { phase: 'loading' }
  | { phase: 'unauthenticated' }
  | { phase: 'forbidden' }
  | { phase: 'ready'; session: BootstrapResponse };

const MEMBERSHIP_RECONCILIATION_MAX_ATTEMPTS = 4;
const MEMBERSHIP_RECONCILIATION_RETRY_MS = 100;

function inventoryIdSetKey(ids: Iterable<string>): string {
  return JSON.stringify([...ids].sort());
}

interface AppProps {
  /** Injected in tests; production creates a BrowserVoiceTransport. */
  testTransport?: VoiceTransport;
}

/**
 * Signed-in web entry point.
 *
 * Conversation is the primary surface at every width: it is first in document order and it is what
 * `WorkspacePanel` puts in the page's `main` landmark, beside the Inventory workspace on a wide
 * viewport and in front of it on a narrow one. The workspace is a read projection and a navigation
 * surface only - browsing an Inventory there never changes which one the conversation is using, and
 * nothing in it can change a quantity.
 *
 * Projections are invalidated by the Participant-level stream rather than by guessing: whenever an
 * Inventory's version moves - because of this conversation, another tab, another Participant, or a
 * future channel - the workspace re-reads the authoritative projection. What this tab learns locally,
 * before the server has published anything, is counted separately, so a local signal can never make
 * the server's next version look like one already seen.
 */
function App({ testTransport }: AppProps = {}) {
  const [state, setState] = useState<SessionState>({ phase: 'loading' });
  const [error, setError] = useState<string | null>(null);

  // Deliberately separate from `error`. A permanently failed Inventory stream is not an ordinary
  // operation error: the stream stays dead until the page is refreshed, no matter what the
  // Participant does next. `error` is cleared at the start of every unrelated handler (create,
  // select, new conversation) the instant it begins its own attempt; if the stream's warning lived
  // there too, any of those actions would silently erase it while the stream stayed just as dead.
  const [inventoryStreamError, setInventoryStreamError] = useState<string | null>(null);
  const [membershipRefreshError, setMembershipRefreshError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [newInventoryName, setNewInventoryName] = useState('');
  const [creating, setCreating] = useState(false);
  const [selectingId, setSelectingId] = useState<string | null>(null);
  const [resetting, setResetting] = useState(false);
  const [conversationEpoch, setConversationEpoch] = useState(0);

  // Two separate namespaces on purpose. `inventoryVersions` holds ONLY what the server published;
  // `localRefetchNonce` counts the times this tab learned something locally - a Turn reaching its
  // Outcome, an import it just applied - that the server has not published a version for yet.
  // Writing a local signal into the server's namespace would be a real defect: the next version the
  // server publishes could then equal one this tab believes it has already seen, and the change
  // behind it would silently never be read.
  const [inventoryVersions, setInventoryVersions] = useState<InventoryVersions>({});
  const [localRefetchNonce, setLocalRefetchNonce] = useState(0);
  const sessionRequestSequence = useRef(0);

  // ── Voice state ──────────────────────────────────────────────────────────────

  // Stable transport reference — never recreated across renders.
  const transportRef = useRef<VoiceTransport>(testTransport ?? new BrowserVoiceTransport());

  // Tracked outside React state so unmount cleanup and async callbacks can read the current value
  // without triggering re-renders or stale closures.
  const voiceSessionIdRef = useRef<string | null>(null);

  // Generation token for fencing late voice callbacks. Incremented on every admission end and
  // new-conversation teardown. Callbacks captured at a prior generation are ignored.
  const voiceGenerationRef = useRef(0);

  // Ref to always-current CSRF token for unmount cleanup (effect with [] deps can't see state).
  const csrfTokenRef = useRef('');

  /**
   * A change this tab knows about before the server announces it. Stable across renders, because it
   * is passed to children whose effects depend on it - an identity that changed every render would
   * make them re-run for no reason, and would make a mid-flight resume look like a fresh mount.
   */
  const invalidateActiveInventory = useCallback(() => setLocalRefetchNonce((nonce) => nonce + 1), []);

  // ── Shared turn submission handle ────────────────────────────────────────────

  // TurnTracer writes its submit function here so voice finalized utterances can use the same
  // controller. Cleared on TurnTracer unmount (key change / conversation reset).
  const submitTurnRef = useRef<((input: TurnSubmissionInput) => boolean) | null>(null);

  // The session values for voice callbacks and unmount cleanup.
  const csrfToken = state.phase === 'ready' ? state.session.csrfToken : '';

  // Keep CSRF ref current for unmount cleanup (effects with [] deps can't see render-time values).
  csrfTokenRef.current = csrfToken;

  // ── Voice callbacks ──────────────────────────────────────────────────────────

  const handleVoiceSessionChanged = useCallback((id: string | null) => {
    voiceSessionIdRef.current = id;
  }, []);

  const handleFinalizedUtterance = useCallback(
    (utterance: FinalizedUtterance) => {
      const sessionId = voiceSessionIdRef.current;
      if (!sessionId) return;

      submitTurnRef.current?.({
        nativeMessageId: utterance.nativeMessageId,
        contentText: utterance.text,
        voiceSessionId: sessionId,
      });
    },
    [],
  );

  const loadSession = useCallback(
    async (reportError: (message: string) => void = setError): Promise<BootstrapResult | null> => {
      const requestSequence = ++sessionRequestSequence.current;

      try {
        const result = await fetchBootstrap();
        if (requestSequence !== sessionRequestSequence.current) {
          return null;
        }

        if (result.status === 'ok') {
          setState({ phase: 'ready', session: result.data });
        } else {
          setState({ phase: result.status });
        }
        return result;
      } catch (err) {
        if (requestSequence === sessionRequestSequence.current) {
          reportError(err instanceof Error ? err.message : String(err));
        }
        return null;
      }
    },
    [],
  );

  useEffect(() => {
    // oxlint(react/set-state-in-effect) only recognizes an inline async IIFE's await boundary, not
    // one behind a named function reference - even though every setState call inside loadSession
    // already happens after its own internal await. Wrapping the call this way keeps loadSession
    // reusable while making that already-true post-await ordering visible to the linter too.
    void (async () => {
      await loadSession();
    })();
  }, [loadSession]);

  // ── Unmount voice cleanup ──────────────────────────────────────────────────
  // Uses refs so the cleanup reads the values at teardown time, not at effect creation time.
  useEffect(() => {
    const transport = transportRef.current;
    return () => {
      voiceGenerationRef.current += 1;
      transport.disconnect();

      const sessionId = voiceSessionIdRef.current;
      const token = csrfTokenRef.current;
      if (sessionId && token) {
        // Best-effort: keepalive + custom CSRF header is not dependable during page unload.
        // SQL idle/expiry is the authoritative cleanup mechanism.
        fetch('/api/voice/release', {
          method: 'POST',
          keepalive: true,
          credentials: 'include',
          headers: {
            'Content-Type': 'application/json',
            'X-CSRF-TOKEN': token,
          },
          body: JSON.stringify({ voiceSessionId: sessionId }),
        }).catch(() => {});
      }
      voiceSessionIdRef.current = null;
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const isReady = state.phase === 'ready';
  const authorizedInventorySetKey =
    state.phase === 'ready'
      ? inventoryIdSetKey(state.session.bootstrap.inventories.map((inventory) => inventory.id))
      : '';

  useEffect(() => {
    if (!isReady) {
      return;
    }

    let stopped = false;
    let reconciliationTarget: string | null = null;
    let reconciliationRunning = false;

    async function reconcileMemberships(): Promise<void> {
      if (reconciliationRunning) {
        return;
      }

      reconciliationRunning = true;
      try {
        let attempts = 0;
        while (
          !stopped &&
          reconciliationTarget !== null &&
          attempts < MEMBERSHIP_RECONCILIATION_MAX_ATTEMPTS
        ) {
          attempts += 1;
          const target = reconciliationTarget;
          const result = await loadSession(setMembershipRefreshError);
          if (stopped || reconciliationTarget !== target) {
            continue;
          }

          if (result !== null) {
            setMembershipRefreshError(null);
            if (result.status !== 'ok') {
              reconciliationTarget = null;
              return;
            }

            const applied = inventoryIdSetKey(
              result.data.bootstrap.inventories.map((inventory) => inventory.id),
            );
            if (applied === target) {
              reconciliationTarget = null;
              return;
            }
          }

          if (attempts < MEMBERSHIP_RECONCILIATION_MAX_ATTEMPTS) {
            const retryDelay = MEMBERSHIP_RECONCILIATION_RETRY_MS * 2 ** (attempts - 1);
            await new Promise((resolve) => setTimeout(resolve, retryDelay));
          }
        }
      } finally {
        reconciliationRunning = false;
      }
    }

    const stream = openInventoryStream({
      onVersions: (versions) => {
        setInventoryVersions(versions);
        setInventoryStreamError(null);

        const nextAuthorizedInventorySetKey = inventoryIdSetKey(Object.keys(versions));
        if (nextAuthorizedInventorySetKey !== authorizedInventorySetKey) {
          reconciliationTarget = nextAuthorizedInventorySetKey;
          void reconcileMemberships();
        } else {
          reconciliationTarget = null;
        }
      },
      // Into the dedicated, persistent state - never into `error` - so that no unrelated handler's
      // `setError(null)` can make this warning disappear while the stream is still just as dead.
      onFailed: () =>
        setInventoryStreamError(
          'Lost the connection to Inventory updates and cannot resync automatically. Refresh the page to try again.',
        ),
    });
    return () => {
      stopped = true;
      stream.close();
    };
  }, [authorizedInventorySetKey, isReady, loadSession]);

  async function handleCreateInventory(event: React.FormEvent) {
    event.preventDefault();
    if (state.phase !== 'ready') {
      return;
    }

    setCreating(true);
    setError(null);
    setNotice(null);

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
    setNotice(null);

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

  async function handleNewConversation() {
    if (state.phase !== 'ready') {
      return;
    }

    setResetting(true);
    setError(null);
    setNotice(null);

    // 1. Capture the current voice session ID before clearing any state.
    const capturedVoiceSessionId = voiceSessionIdRef.current;

    // 2. Fence voice callbacks — increment generation so late callbacks are ignored.
    voiceGenerationRef.current += 1;
    voiceSessionIdRef.current = null;

    // 3. Disconnect transport locally — stops mic/playback/data channel.
    transportRef.current.disconnect();

    try {
      // 4. Execute existing server rotation (unchanged — #35 safety ordering).
      const rotation = await startNewConversation(state.session.csrfToken);

      // 5. Clear recovery storage (unchanged — only after successful rotation).
      const cleared = clearInFlightTurn(
        state.session.bootstrap.webConversationId,
        state.session.bootstrap.participantId,
      );
      if (!cleared) {
        setError(
          'The new conversation started, but browser recovery state could not be cleared safely. The current view was kept open to avoid recovering work from the prior conversation. Try again once browser storage is available.',
        );

        // Still release voice session best-effort even on storage clear failure
        if (capturedVoiceSessionId) {
          try {
            await releaseVoice(capturedVoiceSessionId, state.session.csrfToken);
          } catch {
            /* best-effort — SQL idle/expiry is authoritative */
          }
        }
        return;
      }

      // 6. Release voice session (best-effort — SQL idle/expiry is authoritative).
      if (capturedVoiceSessionId) {
        try {
          await releaseVoice(capturedVoiceSessionId, state.session.csrfToken);
        } catch {
          /* best-effort — SQL idle/expiry is authoritative */
        }
      }

      // 7. Remount conversation + reset voice.
      setConversationEpoch((epoch) => epoch + 1);
      setNotice(
        rotation.clearedPendingConfirmation
          ? 'Started a new conversation. The change that was waiting for confirmation was cleared.'
          : 'Started a new conversation.',
      );
    } catch (err) {
      // Even on rotation failure, voice transport is already disconnected locally.
      // Best-effort release the server session.
      if (capturedVoiceSessionId) {
        try {
          await releaseVoice(capturedVoiceSessionId, state.session.csrfToken);
        } catch {
          /* best-effort — SQL idle/expiry is authoritative */
        }
      }
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setResetting(false);
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
  const activeInventoryId = bootstrap.activeInventoryId;
  const activeInventory = bootstrap.inventories.find((i) => i.id === activeInventoryId);

  // The Active Inventory's own published version, as the server reports it. A change made anywhere -
  // this conversation, another tab, another Participant, a future channel - moves exactly this number.
  const activeInventoryVersion = activeInventoryId ? (inventoryVersions[activeInventoryId] ?? 0) : 0;

  // One number for the projections to key on, derived from both sources. Both only ever increase, so
  // their sum increases whenever either does and can never coincidentally match a value the workspace
  // has already refetched at. Switching Inventories does not need to be handled here: every workspace
  // component's load already depends on the Inventory id it was given.
  const workspaceRefetchToken = activeInventoryVersion + localRefetchNonce;

  // One `role="alert"` node, not two: a screen reader announcing two competing alerts on the same
  // failure is not clearer, and a test asking for "the" alert should never have to disambiguate.
  // The permanent stream warning is listed first because, once it appears, it outlives whatever
  // ordinary operation error came before or after it.
  const alertMessage = [inventoryStreamError, membershipRefreshError, error].filter(Boolean).join(' ');

  const conversation = (
    <>
      {/*
        The conversation is always available, including before an Inventory has been selected: that is
        exactly when a Participant needs the agent to tell them to select one, and hiding the
        conversation would make that guidance unreachable.
      */}
      <TurnTracer
        key={conversationEpoch}
        csrfToken={session.csrfToken}
        webConversationId={bootstrap.webConversationId}
        participantId={bootstrap.participantId}
        onTerminalOutcome={invalidateActiveInventory}
        submitRef={submitTurnRef}
      />
      <VoiceControls
        key={`voice-${conversationEpoch}`}
        transport={transportRef.current}
        csrfToken={session.csrfToken}
        voiceSessionId={voiceSessionIdRef.current}
        onFinalizedUtterance={handleFinalizedUtterance}
        onVoiceSessionChanged={handleVoiceSessionChanged}
      />
    </>
  );

  const workspace = (
    <>
      <section>
        <h2>Your Inventories</h2>
        {bootstrap.needsOnboarding && <p>You don&apos;t belong to any Inventory yet. Create one to get started.</p>}
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
                    // The only thing that ever switches the conversation's Inventory. Reading the list
                    // above, or any projection below, never does.
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

      {activeInventory?.role === 'Owner' && (
        <InventoryGovernance
          key={activeInventory.id}
          inventoryId={activeInventory.id}
          csrfToken={session.csrfToken}
          onOwnershipChanged={() => void loadSession()}
        />
      )}

      {activeInventoryId && <StockWorkspace inventoryId={activeInventoryId} refetchToken={workspaceRefetchToken} />}

      {activeInventoryId && <ReferenceWorkspace inventoryId={activeInventoryId} refetchToken={workspaceRefetchToken} />}

      {/*
        Keyed by the Active Inventory so switching Inventories starts the workflow over rather than
        carrying a preview of one Inventory's file into another: an import proposal is bound to the
        Inventory that issued it, so none of this component's state means anything anywhere else.
      */}
      {activeInventoryId && (
        <InitialImport
          key={activeInventoryId}
          inventoryId={activeInventoryId}
          csrfToken={session.csrfToken}
          refetchToken={workspaceRefetchToken}
          onStockMayHaveChanged={invalidateActiveInventory}
        />
      )}
    </>
  );

  return (
    <>
      <header>
        <h1>Multi-Channel Agent</h1>
        <p>
          Signed in as <strong>{bootstrap.displayName}</strong>
        </p>
        {/*
          Always visible, at every width: an explicit switch that scrolled out of sight would be an
          explicit switch a Participant cannot check.
        */}
        <p>
          Active Inventory: <strong>{activeInventory ? activeInventory.name : 'none selected'}</strong>
        </p>
        <button type="button" onClick={() => void handleNewConversation()} disabled={resetting}>
          {resetting ? 'Starting…' : 'New conversation'}
        </button>
        {alertMessage && <p role="alert">{alertMessage}</p>}
        <p role="status" aria-live="polite">
          {notice ?? ''}
        </p>
      </header>

      <WorkspacePanel conversation={conversation} workspace={workspace} />
    </>
  );
}

export default App;
