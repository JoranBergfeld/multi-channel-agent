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
  type SubmitTurnRequest,
  type TurnOutcomeView,
} from './turnsApi';

export interface TurnSubmissionInput {
  nativeMessageId: string;
  contentText: string;
  wasInterrupted?: boolean;
  voiceSessionId?: string;
}

export type TurnSubmissionProgress = 'idle' | 'submitting' | 'accepted' | 'processing';

export interface UseTurnSubmissionResult {
  submit: (input: TurnSubmissionInput) => boolean;
  progress: TurnSubmissionProgress;
  turnId: string | null;
  parts: TurnResponsePartEvent[];
  outcome: TurnOutcomeView | null;
  error: string | null;
}

export interface UseTurnSubmissionOptions {
  csrfToken: string;
  webConversationId: string;
  participantId: string;
  onTerminalOutcome: () => void;
  createSource?: EventStreamFactory;
  /** When false, the hook is dormant — no resume, no subscription, no streams. Default true. */
  enabled?: boolean;
}

/**
 * Shared turn submission controller. Owns the submission/recovery/stream lifecycle that both text
 * and voice share: breadcrumb persistence, idempotent submit, SSE stream watch, lost-response
 * recovery, cross-tab subscription, and unmount safety. The consumer (TurnTracer, and later voice)
 * retains its own rendering/form responsibilities.
 */
export function useTurnSubmission(options: UseTurnSubmissionOptions): UseTurnSubmissionResult {
  const { csrfToken, webConversationId, participantId, onTerminalOutcome, createSource, enabled = true } = options;

  const [progress, setProgress] = useState<TurnSubmissionProgress>('idle');
  const [turnId, setTurnId] = useState<string | null>(null);
  const [parts, setParts] = useState<TurnResponsePartEvent[]>([]);
  const [outcome, setOutcome] = useState<TurnOutcomeView | null>(null);
  const [error, setError] = useState<string | null>(null);

  const streamRef = useRef<{ close: () => void } | null>(null);
  const watchedTurnRef = useRef<string | null>(null);
  const resumeAttemptedRef = useRef(false);
  const partsRef = useRef<TurnResponsePartEvent[]>([]);
  const mountedRef = useRef(false);
  const fetchInFlightRef = useRef(false);

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
            clearInFlightTurnIfMatches(webConversationId, participantId, { turnId: id });
            onTerminalOutcome();
          },
          onFailed: () => {
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
      watchTurn(stored.turnId);
      return;
    }

    if (stored.contentText === null) {
      clearInFlightTurnIfMatches(webConversationId, participantId, { nativeMessageId: stored.nativeMessageId });
      setError(
        'A confirmation was submitted but could not be resumed automatically. Check the current Inventory state before trying again.',
      );
      return;
    }

    setProgress('submitting');
    fetchInFlightRef.current = true;

    try {
      const result = await submitTurn(
        { nativeMessageId: stored.nativeMessageId, contentText: stored.contentText },
        csrfToken,
      );

      if (!mountedRef.current) {
        fetchInFlightRef.current = false;
        return;
      }

      fetchInFlightRef.current = false;

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
        fetchInFlightRef.current = false;
        return;
      }

      fetchInFlightRef.current = false;

      if (err instanceof SubmitTurnRejectionError && isDefinitiveRejection(err.status)) {
        clearInFlightTurnIfMatches(webConversationId, participantId, { nativeMessageId: stored.nativeMessageId });
      }

      setError(err instanceof Error ? err.message : String(err));
      setProgress('idle');
    }
  }, [csrfToken, onTerminalOutcome, participantId, watchTurn, webConversationId]);

  // Resume on mount — once-per-mount decision guarded by resumeAttemptedRef.
  useEffect(() => {
    if (!enabled) return;

    if (resumeAttemptedRef.current) {
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
  }, [enabled, participantId, resumeStoredTurn, watchTurn, webConversationId]);

  // Cross-tab storage subscription.
  useEffect(
    () => {
      if (!enabled) return;
      return subscribeToConversationChanges(webConversationId, participantId, () => {
        const stored = readInFlightTurn(webConversationId, participantId);
        if (stored?.turnId != null) {
          watchTurn(stored.turnId);
        }
      });
    },
    [enabled, participantId, watchTurn, webConversationId],
  );

  // Stream cleanup on unmount.
  useEffect(
    () => () => {
      streamRef.current?.close();
      streamRef.current = null;
      watchedTurnRef.current = null;
    },
    [],
  );

  // Mounted guard.
  useEffect(() => {
    mountedRef.current = true;

    return () => {
      mountedRef.current = false;
    };
  }, []);

  const submit = useCallback(
    (input: TurnSubmissionInput): boolean => {
      if (fetchInFlightRef.current) {
        return false;
      }

      const { nativeMessageId, contentText } = input;

      if (!rememberSubmission(webConversationId, participantId, { nativeMessageId, contentText })) {
        setError(
          'Browser storage is unavailable, so this message was not sent - safe recovery cannot be guaranteed without it. Try again once storage is available.',
        );
        return false;
      }

      streamRef.current?.close();
      streamRef.current = null;
      watchedTurnRef.current = null;

      setError(null);
      setOutcome(null);
      partsRef.current = [];
      setParts([]);
      setProgress('submitting');
      fetchInFlightRef.current = true;

      const request: SubmitTurnRequest = { nativeMessageId, contentText };
      if (input.wasInterrupted) {
        request.interrupted = true;
      }
      if (input.voiceSessionId !== undefined) {
        request.voiceSessionId = input.voiceSessionId;
      }

      void (async () => {
        try {
          const result = await submitTurn(request, csrfToken);

          if (!mountedRef.current) {
            fetchInFlightRef.current = false;
            return;
          }

          fetchInFlightRef.current = false;

          if (result.kind === 'outcome') {
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
            fetchInFlightRef.current = false;
            return;
          }

          fetchInFlightRef.current = false;

          if (err instanceof SubmitTurnRejectionError && isDefinitiveRejection(err.status)) {
            clearInFlightTurnIfMatches(webConversationId, participantId, { nativeMessageId });
          }

          setError(err instanceof Error ? err.message : String(err));
          setProgress('idle');
        }
      })();

      return true;
    },
    [csrfToken, onTerminalOutcome, participantId, watchTurn, webConversationId],
  );

  return { submit, progress, turnId, parts, outcome, error };
}
