import { useCallback, useEffect, useRef, useState } from 'react';
import { getTurnOutcome, submitTurn, type TurnOutcomeView } from './turnsApi';

const POLL_INTERVAL_MS = 1500;

/** Minimal tracer UI: submit a synthetic Turn and watch its recorded Outcome arrive. */
function TurnTracer() {
  const [conversationId] = useState(() => crypto.randomUUID());
  const [contentText, setContentText] = useState('hello from the web client');
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
        }
      } catch (err) {
        setError(err instanceof Error ? err.message : String(err));
        stopPolling();
      }
    }, POLL_INTERVAL_MS);
  }, [stopPolling]);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    setOutcome(null);

    try {
      const result = await submitTurn({
        nativeMessageId: crypto.randomUUID(),
        channelConversationId: conversationId,
        contentText,
      });
      setTurnId(result.turnId);
      pollOutcome(result.turnId);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <section>
      <h2>Turn Tracer</h2>
      <p>
        Submits a normalized synthetic Turn to the application boundary and displays its recorded
        terminal Outcome once processing completes.
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
          {submitting ? 'Submitting…' : 'Submit Turn'}
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
          <h2>Outcome</h2>
          <dl>
            <dt>Status</dt>
            <dd>{outcome.status}</dd>
            <dt>Code</dt>
            <dd>{outcome.code}</dd>
            <dt>Summary</dt>
            <dd>{outcome.summary}</dd>
          </dl>

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
