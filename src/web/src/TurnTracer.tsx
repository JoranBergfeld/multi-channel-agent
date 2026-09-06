import { useEffect, useState } from 'react';
import type { EventStreamFactory } from './turnStream';
import {
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
} from './turnsApi';
import { useTurnSubmission, type TurnSubmissionInput, type TurnSubmissionProgress } from './useTurnSubmission';

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
  onTerminalOutcome: (outcome: import('./turnsApi').TurnOutcomeView) => void;
  /** Swapped in tests for a controllable double, since jsdom implements no EventSource. */
  createSource?: EventStreamFactory;
  /** When provided, TurnTracer writes its submit function to this ref so voice (or other callers
   * at the App level) can submit through the same controller. Cleared on unmount. */
  submitRef?: React.MutableRefObject<((input: TurnSubmissionInput) => boolean) | null>;
}

const PROGRESS_TEXT: Record<Exclude<TurnSubmissionProgress, 'idle'>, string> = {
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
function TurnTracer({ csrfToken, webConversationId, participantId, onTerminalOutcome, createSource, submitRef }: TurnTracerProps) {
  const [contentText, setContentText] = useState('list stock');
  const { submit, progress, turnId, parts, outcome, error } = useTurnSubmission({
    csrfToken,
    webConversationId,
    participantId,
    onTerminalOutcome,
    createSource,
  });

  // Expose the submit handle so App-level voice can call the same controller.
  useEffect(() => {
    if (submitRef) submitRef.current = submit;
    return () => { if (submitRef) submitRef.current = null; };
  }, [submit, submitRef]);

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    const nativeMessageId = crypto.randomUUID();
    submit({ nativeMessageId, contentText });
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
