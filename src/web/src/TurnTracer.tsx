import { useCallback, useEffect, useRef, useState } from 'react';
import {
  getTurnOutcome,
  submitTurn,
  type StockMutationPayload,
  type StockNarrowingHints,
  type StockRowView,
  type TurnOutcomeView,
} from './turnsApi';

const POLL_INTERVAL_MS = 1500;

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

interface TurnTracerProps {
  csrfToken: string;
  /** Called once a terminal Outcome arrives, so the workspace can refetch its authoritative projection. */
  onTerminalOutcome: () => void;
}

/**
 * Submits a Turn to the application boundary and renders its recorded terminal Outcome, including
 * the typed semantic List/Find/mutation payload when the Outcome carries one. Every terminal Outcome
 * also signals the parent, which is what invalidates and refetches the authoritative Inventory
 * workspace - so a mutation made in the conversation is visible in the workspace immediately.
 * Participant/ChannelConversation identity is always derived server-side; this component never
 * supplies either.
 */
function TurnTracer({ csrfToken, onTerminalOutcome }: TurnTracerProps) {
  const [contentText, setContentText] = useState('list stock');
  const [submitting, setSubmitting] = useState(false);
  const [turnId, setTurnId] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<TurnOutcomeView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const pollHandle = useRef<number | undefined>(undefined);

  const stopPolling = useCallback(() => {
    if (pollHandle.current !== undefined) {
      window.clearInterval(pollHandle.current);
      pollHandle.current = undefined;
    }
  }, []);

  useEffect(() => stopPolling, [stopPolling]);

  const pollOutcome = useCallback((id: string) => {
    stopPolling();
    pollHandle.current = window.setInterval(async () => {
      try {
        const result = await getTurnOutcome(id);
        if (result) {
          setOutcome(result);
          stopPolling();
          onTerminalOutcome();
        }
      } catch (err) {
        setError(err instanceof Error ? err.message : String(err));
        stopPolling();
      }
    }, POLL_INTERVAL_MS);
  }, [stopPolling, onTerminalOutcome]);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    setOutcome(null);

    try {
      const result = await submitTurn({ nativeMessageId: crypto.randomUUID(), contentText }, csrfToken);

      if (result.kind === 'outcome') {
        // This exact native message was already answered, so its recorded terminal Outcome came
        // back with the submission itself - there is nothing left to wait for.
        setTurnId(result.outcome.turnId);
        setOutcome(result.outcome);
        onTerminalOutcome();
        return;
      }

      setTurnId(result.acceptance.turnId);
      pollOutcome(result.acceptance.turnId);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSubmitting(false);
    }
  }

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
      <form onSubmit={handleSubmit}>
        <label htmlFor="contentText">Message</label>
        <textarea
          id="contentText"
          value={contentText}
          onChange={(event) => setContentText(event.target.value)}
          rows={3}
        />
        <button type="submit" disabled={submitting}>
          {submitting ? 'Submitting…' : 'Send'}
        </button>
      </form>

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

      {turnId && !outcome && !error && <p>Waiting for the terminal Outcome…</p>}

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
