import { useCallback, useEffect, useRef, useState } from 'react';
import {
  clearInFlightTurnIfMatches,
  readInFlightTurn,
  rememberSubmission,
  rememberTurnId,
  subscribeToConversationChanges,
} from './conversationStorage';
import { openTurnStream, type EventStreamFactory, type TurnResponsePartEvent } from './turnStream';
import {
  composeOutcome,
  isDefinitiveRejection,
  submitTurn,
  SubmitTurnRejectionError,
  type StockChangeView,
  type StockChangesPayload,
  type StockMutationPayload,
  type StockNarrowingHints,
  type StockProposalPayload,
  type StockRowView,
  type ReferenceChangeView,
  type ReferenceChangesPayload,
  type ReferenceProposalPayload,
  type ReferenceSuggestionsPayload,
  type TurnOutcomeView,
} from './turnsApi';

function StockRows({ rows }: { rows: StockRowView[] }) {
  return (
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
        {rows.map((row) => (
          <tr key={row.id}>
            <td>{row.name}</td>
            <td>{row.unit}</td>
            <td>{row.location ?? '—'}</td>
            <td>{row.quantity}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function NarrowingHints({ hints }: { hints: StockNarrowingHints }) {
  const suggestions: string[] = [];
  if (hints.units.length > 0) {
    suggestions.push(`unit (${hints.units.join(', ')})`);
  }
  if (hints.locations.length > 0) {
    suggestions.push(`location (${hints.locations.join(', ')})`);
  }
  if (hints.includesUnlocated) {
    suggestions.push('unlocated stock');
  }

  if (suggestions.length === 0) {
    return null;
  }

  return <p>Narrow by {suggestions.join(' or ')}.</p>;
}

function StockMutationResult({ payload }: { payload: StockMutationPayload }) {
  const { entry } = payload;

  return (
    <>
      <h3>{entry.created ? 'Created' : 'Updated'}</h3>
      <dl>
        <dt>Stock Entry</dt>
        <dd>{entry.name}</dd>
        <dt>Unit</dt>
        <dd>{entry.unit}</dd>
        <dt>Location</dt>
        <dd>{entry.location ?? 'Unlocated'}</dd>
        <dt>Quantity</dt>
        <dd>
          {entry.previousQuantity} → {entry.quantity}
        </dd>
        {entry.note !== null && (
          <>
            <dt>Note</dt>
            <dd>{entry.note}</dd>
          </>
        )}
      </dl>
      {entry.notePreserved && <p>The existing Note was kept unchanged.</p>}
    </>
  );
}

/** What one change does, in the same words the conversational answer uses. */
const EFFECT_LABELS: Record<StockChangeView['effect'], string> = {
  created: 'Create',
  quantity_increased: 'Add',
  quantity_decreased: 'Remove',
  quantity_set: 'Set',
  quantity_cleared: 'Clear',
  placed: 'Move',
  split: 'Move part',
  split_merged: 'Move part and merge',
  merged: 'Move all and merge',
  renamed: 'Rename',
  rename_merged: 'Rename and merge',
  forgotten: 'Forget',
};

function placementOf(state: { location: string | null }) {
  return state.location ?? 'Unlocated';
}

/**
 * Every change of a proposal or an applied change set, exactly. The Identity column is the one a
 * merge-retiring Move or Rename owes the Participant: which Stock Entry survives, and which one's
 * identity ends.
 */
function StockChangeRows({ changes }: { changes: StockChangeView[] }) {
  return (
    <table>
      <thead>
        <tr>
          <th>Change</th>
          <th>Stock Entry</th>
          <th>Quantity</th>
          <th>Identity</th>
        </tr>
      </thead>
      <tbody>
        {changes.map((change) => (
          <tr key={change.order}>
            <td>{EFFECT_LABELS[change.effect]}</td>
            <td>
              <div>
                {change.source.name} ({placementOf(change.source)})
              </div>
              {change.destination && (
                <div>
                  → {change.newName ?? change.destination.name} ({placementOf(change.destination)})
                </div>
              )}
            </td>
            <td>
              <div>
                {change.source.previousQuantity} → {change.source.quantity} {change.source.unit}
              </div>
              {change.destination && (
                <div>
                  {change.destination.previousQuantity} → {change.destination.quantity} {change.destination.unit}
                </div>
              )}
            </td>
            <td>
              <div>Survives: {change.survivingStockEntryId ?? 'nothing'}</div>
              {change.retiredStockEntryId !== null && <div>Retires: {change.retiredStockEntryId}</div>}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/**
 * The exact changes awaiting confirmation. The buttons fill the message box rather than submitting:
 * the server only ever accepts an affirmative the Participant themselves said in their own next
 * Turn's direct content, so a button that submitted for them would be approving on their behalf.
 */
function StockProposal({
  payload,
  onCommand,
}: {
  payload: StockProposalPayload;
  onCommand: (command: string) => void;
}) {
  return (
    <>
      <h3>Confirm these changes</h3>
      <StockChangeRows changes={payload.changes} />
      <p>Expires at {new Date(payload.expiresAt).toLocaleTimeString()}</p>
      <button type="button" onClick={() => onCommand(`confirm ${payload.token}`)}>
        Confirm
      </button>
      <button type="button" onClick={() => onCommand('reject')}>
        Reject
      </button>
    </>
  );
}

function StockChanges({ payload }: { payload: StockChangesPayload }) {
  return (
    <>
      <h3>Applied</h3>
      <StockChangeRows changes={payload.changes} />
    </>
  );
}

function ReferenceChangeRows({ changes }: { changes: ReferenceChangeView[] }) {
  return (
    <table>
      <thead>
        <tr>
          <th>Change</th>
          <th>Reference</th>
          <th>Name</th>
          <th>Result</th>
        </tr>
      </thead>
      <tbody>
        {changes.map((change) => (
          <tr key={change.order}>
            <td>{change.operation.replaceAll('_', ' ')}</td>
            <td>{change.reference}</td>
            <td>{change.name}</td>
            <td>
              {change.newName ?? change.alias ?? (change.aliases.length > 0 ? change.aliases.join(', ') : '—')}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function ReferenceProposal({
  payload,
  onCommand,
}: {
  payload: ReferenceProposalPayload;
  onCommand: (command: string) => void;
}) {
  return (
    <section>
      <h3>Confirm this change to Units and Locations</h3>
      <ReferenceChangeRows changes={payload.changes} />
      <p>Expires at {new Date(payload.expiresAt).toLocaleTimeString()}</p>
      <button type="button" onClick={() => onCommand(`confirm ${payload.token}`)}>
        Confirm
      </button>
      <button type="button" onClick={() => onCommand('reject')}>
        Reject
      </button>
    </section>
  );
}

function ReferenceChanges({ payload }: { payload: ReferenceChangesPayload }) {
  return (
    <section>
      <h3>Applied</h3>
      <ReferenceChangeRows changes={payload.changes} />
    </section>
  );
}

function ReferenceSuggestions({ payload }: { payload: ReferenceSuggestionsPayload }) {
  return (
    <section>
      <h3>No such {payload.reference}</h3>
      {payload.suggestions.length === 0 ? (
        <p>This Inventory has no {payload.reference}s yet.</p>
      ) : (
        <ul>
          {payload.suggestions.map((suggestion) => (
            <li key={suggestion}>{suggestion}</li>
          ))}
        </ul>
      )}
    </section>
  );
}

interface TurnTracerProps {
  csrfToken: string;
  /** This browser profile's stable web conversation identity, from the session bootstrap. */
  webConversationId: string;
  /** The signed-in Participant's stable identity, from the session bootstrap. */
  participantId: string;
  /** Called once a terminal Outcome arrives, so the workspace can refetch its authoritative projection. */
  onTerminalOutcome: () => void;
  /** Swapped in tests for a controllable double, since jsdom implements no EventSource. */
  createSource?: EventStreamFactory;
}

/** What this conversation is currently doing, for the live region that announces it. */
type ConversationProgress = 'idle' | 'submitting' | 'accepted' | 'processing';

const PROGRESS_TEXT: Record<Exclude<ConversationProgress, 'idle'>, string> = {
  submitting: 'Sending your message…',
  accepted: 'Accepted. Waiting for it to be picked up…',
  processing: 'Working on it…',
};

/**
 * The conversation: submits a Turn, follows its finite resumable event stream, and renders the
 * semantic parts and terminal Outcome it carries.
 *
 * It resumes rather than resubmits. On mount - after a refresh, a restart, or in a second tab - it
 * looks for this browser profile's unfinished Turn and reconnects that Turn's stream, which is a pure
 * read. Only in the one case where the browser never learned the Turn id at all does it submit again,
 * and then with the very same native message id, which the application boundary answers from the
 * Turn it already recorded rather than by doing the work twice. That is what makes reconnecting to
 * mutation-capable work safe.
 *
 * Participant and ChannelConversation identity are always derived server-side; this component never
 * supplies either, and it holds no token of any kind.
 */
function TurnTracer({ csrfToken, webConversationId, participantId, onTerminalOutcome, createSource }: TurnTracerProps) {
  const [contentText, setContentText] = useState('list stock');
  const [progress, setProgress] = useState<ConversationProgress>('idle');
  const [turnId, setTurnId] = useState<string | null>(null);
  const [parts, setParts] = useState<TurnResponsePartEvent[]>([]);
  const [outcome, setOutcome] = useState<TurnOutcomeView | null>(null);
  const [error, setError] = useState<string | null>(null);

  const streamRef = useRef<{ close: () => void } | null>(null);
  const watchedTurnRef = useRef<string | null>(null);

  // Resuming is a once-per-mount decision, not a once-per-render one. In the window where a stored
  // submission has no Turn id yet, resuming means re-POSTing - safe, because the native message id is
  // an idempotency key, but pointless work and a second in-flight request. A parent that re-renders
  // with fresh callback identities would otherwise re-run the effect and do exactly that.
  const resumeAttemptedRef = useRef(false);

  // The parts as they arrive, mirrored outside React state so the terminal handler can compose the
  // Outcome from them without reading state inside a state updater - which React may run twice.
  const partsRef = useRef<TurnResponsePartEvent[]>([]);

  // True whenever this component is actually mounted - including through React StrictMode's
  // development-only mount/cleanup/mount, which flips it false and back true the same way it flips
  // streamRef/watchedTurnRef below. Every async continuation checks it immediately after its await,
  // before touching state, storage, a stream, or the parent callback - so a response that arrives
  // after a real unmount can never act as though this component were still here to receive it.
  const mountedRef = useRef(false);

  const watchTurn = useCallback(
    (id: string) => {
      if (watchedTurnRef.current === id) {
        return;
      }

      streamRef.current?.close();
      watchedTurnRef.current = id;

      partsRef.current = [];
      setTurnId(id);
      setParts([]);
      setOutcome(null);
      setProgress('accepted');

      streamRef.current = openTurnStream({
        turnId: id,
        handlers: {
          onAccepted: () => setProgress('accepted'),
          onProcessing: () => setProgress('processing'),
          onPart: (part) => {
            partsRef.current = [...partsRef.current, part];
            setParts(partsRef.current);
          },
          onOutcome: (terminal) => {
            setOutcome(composeOutcome(partsRef.current, terminal));
            setProgress('idle');
            // Only if the stored record still names *this* Turn. A superseded Turn's own belated
            // completion must never clear the newer Turn a Participant has since submitted -
            // `handleSubmit` already closed this stream the instant that happened, so in the
            // ordinary case this fires only for the Turn that is genuinely still current, but the
            // check is what makes that true rather than assumed.
            clearInFlightTurnIfMatches(webConversationId, participantId, { turnId: id });
            onTerminalOutcome();
          },
          onFailed: () => {
            // The connection is permanently gone (a 401/403/404, or a response that was never an
            // event stream at all) rather than the transient drop the browser recovers from on its
            // own - the same error state every other failure in this component already renders, so
            // there is nothing new to build, only this one more way of reaching it.
            setError('Lost the connection to this Turn and cannot resume it automatically. Refresh to try again.');
            setProgress('idle');
          },
        },
        factory: createSource,
      });
    },
    [createSource, onTerminalOutcome, participantId, webConversationId],
  );

  const resumeStoredTurn = useCallback(async () => {
    const stored = readInFlightTurn(webConversationId, participantId);
    if (stored === null) {
      return;
    }

    if (stored.turnId !== null) {
      // A pure read. Reconnecting never resubmits. Works the same whether or not contentText was
      // redacted, since nothing here needs it.
      watchTurn(stored.turnId);
      return;
    }

    if (stored.contentText === null) {
      // A confirmation's token is deliberately never persisted (see conversationStorage's own
      // redaction), so its response being lost leaves nothing safe to resubmit - guessing or
      // reconstructing the command is not an option, and this is the one narrow case that cannot be
      // resumed automatically. Clear the record - compare-based, only if it is still this exact
      // submission - so this state is never repeatedly re-attempted, and say so plainly rather than
      // silently doing nothing.
      clearInFlightTurnIfMatches(webConversationId, participantId, { nativeMessageId: stored.nativeMessageId });
      setError(
        'A confirmation was submitted but could not be resumed automatically. Check the current Inventory state before trying again.',
      );
      return;
    }

    // The submission's response was never seen, so it is unknown whether the Turn exists. Sending the
    // same native message id again is the safe way to find out: the boundary is idempotent within
    // this Participant and conversation, so it either accepts it once or hands back what it recorded.
    setContentText(stored.contentText);
    setProgress('submitting');

    try {
      const result = await submitTurn(
        { nativeMessageId: stored.nativeMessageId, contentText: stored.contentText },
        csrfToken,
      );

      if (!mountedRef.current) {
        // A real unmount, not StrictMode's simulated one - that always flips mountedRef back to
        // true well before any awaited response could arrive. Nothing is left to act on: no state
        // to set, no stream to open, no parent to notify.
        return;
      }

      if (result.kind === 'outcome') {
        setTurnId(result.outcome.turnId);
        setOutcome(result.outcome);
        setProgress('idle');
        clearInFlightTurnIfMatches(webConversationId, participantId, { nativeMessageId: stored.nativeMessageId });
        onTerminalOutcome();
        return;
      }

      rememberTurnId(webConversationId, participantId, stored.nativeMessageId, result.acceptance.turnId);
      watchTurn(result.acceptance.turnId);
    } catch (err) {
      if (!mountedRef.current) {
        return;
      }

      if (err instanceof SubmitTurnRejectionError && isDefinitiveRejection(err.status)) {
        // This exact resubmission will never succeed by retrying it - clear it so a future mount
        // does not keep resubmitting a doomed request forever. Compare-based: only if it is still
        // this exact submission's own record.
        clearInFlightTurnIfMatches(webConversationId, participantId, { nativeMessageId: stored.nativeMessageId });
      }

      setError(err instanceof Error ? err.message : String(err));
      setProgress('idle');
    }
  }, [csrfToken, onTerminalOutcome, participantId, watchTurn, webConversationId]);

  useEffect(() => {
    if (resumeAttemptedRef.current) {
      // Already decided once whether to reconnect or resubmit - never repeat that decision, since
      // repeating it for the unknown-Turn-id case would resubmit. But React StrictMode's
      // development-only mount/cleanup/mount may have closed and released a stream this same
      // effect opened moments ago (see the close-on-unmount effect below), leaving a known Turn id
      // with nothing watching it. Recovering that is always a pure read, so it is always safe to
      // repeat: `watchTurn` itself is a no-op if a live stream for this id already exists.
      const stored = readInFlightTurn(webConversationId, participantId);
      if (stored?.turnId != null) {
        watchTurn(stored.turnId);
      }
      return;
    }

    resumeAttemptedRef.current = true;

    void (async () => {
      await resumeStoredTurn();
    })();
  }, [participantId, resumeStoredTurn, watchTurn, webConversationId]);

  useEffect(
    () =>
      subscribeToConversationChanges(webConversationId, participantId, () => {
        // Another tab of this browser profile submitted a Turn, or started a new conversation. Both
        // are changes to the one conversation they share, so this tab follows.
        const stored = readInFlightTurn(webConversationId, participantId);
        if (stored?.turnId != null) {
          watchTurn(stored.turnId);
        }
      }),
    [participantId, watchTurn, webConversationId],
  );

  useEffect(
    () => () => {
      // Symmetric with `watchTurn` taking ownership: release both the stream and the ids that
      // guard against re-acquiring it, so a subsequent mount - a real one, or the second half of
      // StrictMode's development-only mount/cleanup/mount - starts from a clean slate instead of
      // believing a now-closed stream is still live.
      streamRef.current?.close();
      streamRef.current = null;
      watchedTurnRef.current = null;
    },
    [],
  );

  useEffect(() => {
    mountedRef.current = true;

    return () => {
      mountedRef.current = false;
    };
  }, []);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();

    // A newer Turn supersedes whatever this component was watching. Closing it here - before this
    // submission's own record is even written - guarantees the old stream can never deliver another
    // event: openTurnStream gates every handler behind its own closed flag the instant close() is
    // called, synchronously, before this function does anything else. Without this, a queued event
    // from the superseded Turn - most dangerously its own terminal Outcome - could still fire after
    // this one exists and clear or overwrite it.
    streamRef.current?.close();
    streamRef.current = null;
    watchedTurnRef.current = null;

    setError(null);
    setOutcome(null);
    partsRef.current = [];
    setParts([]);
    setProgress('submitting');

    const nativeMessageId = crypto.randomUUID();

    // Recorded BEFORE the request leaves, so a response that never arrives still leaves this browser
    // profile holding the idempotency key it submitted under. If this fails, sending anyway would
    // leave mutation-capable work in flight with nothing anywhere to recover or de-duplicate it by -
    // so nothing is sent at all, and the Participant is told plainly rather than left wondering why
    // nothing happened.
    if (!rememberSubmission(webConversationId, participantId, { nativeMessageId, contentText })) {
      setProgress('idle');
      setError(
        'Browser storage is unavailable, so this message was not sent - safe recovery cannot be guaranteed without it. Try again once storage is available.',
      );
      return;
    }

    try {
      const result = await submitTurn({ nativeMessageId, contentText }, csrfToken);

      if (!mountedRef.current) {
        // A real unmount. Nothing is left to act on: no state to set, no stream to open, no parent
        // to notify.
        return;
      }

      if (result.kind === 'outcome') {
        // This exact native message was already answered, so its recorded terminal Outcome came back
        // with the submission itself - there is nothing left to wait for.
        setTurnId(result.outcome.turnId);
        setOutcome(result.outcome);
        setProgress('idle');
        clearInFlightTurnIfMatches(webConversationId, participantId, { nativeMessageId });
        onTerminalOutcome();
        return;
      }

      rememberTurnId(webConversationId, participantId, nativeMessageId, result.acceptance.turnId);
      watchTurn(result.acceptance.turnId);
    } catch (err) {
      if (!mountedRef.current) {
        return;
      }

      if (err instanceof SubmitTurnRejectionError && isDefinitiveRejection(err.status)) {
        // This exact submission will never succeed by retrying it - clear it so a future mount does
        // not keep resubmitting a doomed request forever. Compare-based: only if it is still this
        // exact submission's own record.
        clearInFlightTurnIfMatches(webConversationId, participantId, { nativeMessageId });
      }

      setError(err instanceof Error ? err.message : String(err));
      setProgress('idle');
    }
  }

  const streamedText = parts.filter((part) => part.kind === 'text' && part.text !== null);

  return (
    <section>
      <h2>Conversation</h2>
      <p>
        Read: <code>list stock</code>, <code>list stock including zero</code>, <code>list stock in &lt;location&gt;</code>,{' '}
        <code>list stock unit &lt;unit&gt;</code>, <code>list stock unlocated</code>,{' '}
        <code>list stock page size &lt;n&gt;</code>, or <code>find &lt;name&gt;</code>.
      </p>
      <p>
        Change: <code>add stock &lt;name&gt; quantity &lt;n&gt;</code>,{' '}
        <code>remove stock &lt;name&gt; quantity &lt;n&gt;</code>, or <code>set stock &lt;name&gt; quantity &lt;n&gt;</code>.
        Add <code>unit &lt;unit&gt;</code>, <code>in &lt;location&gt;</code>, <code>unlocated</code>, or{' '}
        <code>note &lt;text&gt;</code> to any of them.
      </p>
      <p>
        Confirm: <code>move stock Steel Bolts all to Shelf A</code>,{' '}
        <code>rename stock Steel Bolts to Brass Rivets</code>, <code>forget stock Steel Bolts</code>, or{' '}
        <code>change stock: add Steel Bolts quantity 2; forget Brass Rivets</code>. Anything that clears, merges, or
        forgets asks first - answer with <code>confirm &lt;code&gt;</code> or <code>reject</code>.
      </p>
      <form onSubmit={handleSubmit}>
        <label htmlFor="contentText">Message</label>
        <textarea
          id="contentText"
          value={contentText}
          onChange={(event) => setContentText(event.target.value)}
          rows={3}
        />
        <button type="submit" disabled={progress === 'submitting'}>
          Send
        </button>
      </form>

      {/*
        Announced rather than only shown: progress that a screen reader never hears is progress a
        Participant using one does not get.
      */}
      <p role="status" aria-live="polite">
        {progress === 'idle' ? '' : PROGRESS_TEXT[progress]}
      </p>

      {turnId && (
        <section>
          <h2>Turn</h2>
          <p>
            <code>{turnId}</code>
          </p>
        </section>
      )}

      {error && (
        <section role="alert">
          <h2>Error</h2>
          <p>{error}</p>
        </section>
      )}

      {streamedText.length > 0 && !outcome && (
        <section>
          <h2>Answer so far</h2>
          {streamedText.map((part) => (
            <p key={part.order}>{part.text}</p>
          ))}
        </section>
      )}

      {outcome && (
        <section>
          <h2>Result</h2>
          <dl>
            <dt>Status</dt>
            <dd>{outcome.status}</dd>
            <dt>Result</dt>
            <dd>{outcome.category}</dd>
            <dt>Code</dt>
            <dd>{outcome.code}</dd>
            <dt>Summary</dt>
            <dd>{outcome.summary}</dd>
          </dl>

          {outcome.payload?.kind === 'stock_list' && (
            <>
              <h3>Stock</h3>
              <StockRows rows={outcome.payload.rows} />
              {outcome.payload.hasMore && <p>More rows are available.</p>}
            </>
          )}

          {outcome.payload?.kind === 'stock_find' && (
            <>
              <h3>Candidates</h3>
              <StockRows rows={outcome.payload.candidates} />
              {outcome.payload.hasMoreCandidates && (
                <p>More matched than are shown here - narrow your request to see the rest.</p>
              )}
              <NarrowingHints hints={outcome.payload.narrowingHints} />
            </>
          )}

          {outcome.payload?.kind === 'stock_mutation' && <StockMutationResult payload={outcome.payload} />}

          {outcome.payload?.kind === 'stock_proposal' && (
            <StockProposal payload={outcome.payload} onCommand={setContentText} />
          )}

          {outcome.payload?.kind === 'stock_changes' && <StockChanges payload={outcome.payload} />}

          {outcome.payload?.kind === 'unit_list' && (
            <section>
              <h3>Units</h3>
              <ul>
                {outcome.payload.units.map((unit) => (
                  <li key={unit.id}>
                    {unit.name}
                    {unit.aliases.length > 0 && ` (${unit.aliases.join(', ')})`}
                  </li>
                ))}
              </ul>
              {outcome.payload.hasMore && <p>More Units are available.</p>}
            </section>
          )}

          {outcome.payload?.kind === 'location_list' && (
            <section>
              <h3>Locations</h3>
              <ul>
                {outcome.payload.locations.map((location) => (
                  <li key={location.id}>{location.name}</li>
                ))}
              </ul>
              {outcome.payload.hasMore && <p>More Locations are available.</p>}
            </section>
          )}

          {outcome.payload?.kind === 'reference_proposal' && (
            <ReferenceProposal payload={outcome.payload} onCommand={setContentText} />
          )}

          {outcome.payload?.kind === 'reference_changes' && <ReferenceChanges payload={outcome.payload} />}

          {outcome.payload?.kind === 'reference_suggestions' && <ReferenceSuggestions payload={outcome.payload} />}

          {outcome.deliveries.length > 0 && (
            <>
              <h3>Deliveries</h3>
              <ul>
                {outcome.deliveries.map((delivery) => (
                  <li key={delivery.deliveryId}>
                    {delivery.channel}: {delivery.status} ({delivery.attempts} attempt(s))
                  </li>
                ))}
              </ul>
            </>
          )}
        </section>
      )}
    </section>
  );
}

export default TurnTracer;
