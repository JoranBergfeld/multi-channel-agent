import type { DeliveryView, TurnOutcomePayload } from './turnsApi'

/**
 * The same fixed sequence identities `TurnEventSequence` issues server-side (see
 * `MultiChannelAgent.Domain.Turns.TurnEventSequence`). `firstPart` is the lowest of a sparse block
 * reserved for response parts - a stream never carries more than a couple, but the identities stay
 * stable even if more are added later.
 */
export const TURN_EVENT_SEQUENCE = {
  accepted: 1,
  processing: 2,
  firstPart: 100,
  outcome: 1_000_000,
} as const

/**
 * The numeric value of the browser's `EventSource.readyState` once a connection has permanently
 * failed. Written as a literal rather than read off the global `EventSource` constructor so this
 * module never depends on that global being defined - a fake source in tests supplies the same
 * number without needing to exist as a real constructor.
 */
const EVENT_SOURCE_CLOSED = 2

export interface TurnAcceptedEvent {
  turnId: string
  receivedAt: string
}

export interface TurnProcessingEvent {
  turnId: string
  startedAt: string
}

/** One channel-neutral piece of the answer. Exactly one of `text` and `payload` is ever present. */
export interface TurnResponsePartEvent {
  turnId: string
  order: number
  kind: 'text' | 'data'
  text: string | null
  payload: TurnOutcomePayload | null
}

export interface TurnStreamOutcomeEvent {
  turnId: string
  status: string
  category: string
  code: string
  summary: string
  deliveries: DeliveryView[]
}

export interface TurnStreamHandlers {
  onAccepted?: (event: TurnAcceptedEvent) => void
  onProcessing?: (event: TurnProcessingEvent) => void
  onPart?: (event: TurnResponsePartEvent) => void
  onOutcome?: (event: TurnStreamOutcomeEvent) => void
  /**
   * Called on a transient connection failure. Purely informational: the browser's own EventSource
   * reconnects by itself with `Last-Event-ID` set to the last identity it observed, so a caller
   * only ever uses this to reflect connection state, never to react by reconnecting itself.
   */
  onDisconnected?: () => void
  /**
   * Called once the connection has failed permanently - the browser has set `readyState` to
   * `CLOSED` and given up reconnecting on its own (a 401/403/404 response, or a response that is
   * not `text/event-stream`, all end this way). Unlike `onDisconnected`, the stream is over: a
   * caller needs to surface this rather than silently keep waiting for events that will never come.
   */
  onFailed?: () => void
}

/** The minimal shape of the browser's `EventSource` this client depends on. */
export interface EventStreamSource {
  addEventListener(type: string, listener: (event: MessageEvent<string>) => void): void
  close(): void
  onerror: ((event: Event) => void) | null
  /** 0 (`CONNECTING`), 1 (`OPEN`), or 2 (`CLOSED`) - mirrors the real `EventSource.readyState`. */
  readonly readyState: number
}

export type EventStreamFactory = (url: string) => EventStreamSource

export interface TurnStream {
  /** The greatest event identity observed so far - what a caller persists to resume later. */
  lastEventId(): number
  close(): void
}

export interface OpenTurnStreamOptions {
  turnId: string
  /** The identity to resume from, or 0 (the default) to open the stream from its beginning. */
  lastEventId?: number
  handlers?: TurnStreamHandlers
  /** Defaults to the real `EventSource`; tests supply a fake instead. */
  factory?: EventStreamFactory
}

/**
 * Opens one Turn's finite, resumable event stream and dispatches each named SSE event to its typed
 * handler.
 *
 * A positive `lastEventId` is sent as the `lastEventId` query parameter rather than a header,
 * because this always represents a deliberate resume (a fresh page load, or a caller reopening
 * after a gap) - the header is reserved for the browser's own automatic reconnect, which this
 * function never has to construct by hand. On the very first connection there is nothing to resume,
 * so the URL carries no query at all.
 */
export function openTurnStream(options: OpenTurnStreamOptions): TurnStream {
  const { turnId, handlers = {} } = options
  const factory = options.factory ?? ((url: string) => new EventSource(url))
  let seen = options.lastEventId ?? 0
  let closed = false

  const url = seen > 0 ? `/api/turns/${turnId}/events?lastEventId=${seen}` : `/api/turns/${turnId}/events`
  const source = factory(url)

  // Only a safe-integer identity greater than what is already recorded ever moves the resume
  // point - an out-of-order, non-numeric, non-integer, or unsafely-large id (which should never
  // happen against this server, but a client should not trust the wire blindly) simply leaves it
  // where it was. `Number.isSafeInteger` rejects non-integers (a fractional id makes no sense for
  // a monotonic counter) and anything beyond `MAX_SAFE_INTEGER`, where `Number` can no longer tell
  // adjacent integers apart - accepting one of those could silently corrupt the resume point with
  // a value neither this id nor any real one the server ever issued.
  function observe(rawId: string): void {
    const id = Number(rawId)
    if (Number.isSafeInteger(id) && id > seen) {
      seen = id
    }
  }

  function on<T>(type: string, handler: ((event: T) => void) | undefined, terminal = false): void {
    source.addEventListener(type, (event) => {
      // A caller's own close() must suppress anything still in flight - the underlying source's
      // close() does not stop events already queued for delivery.
      if (closed) {
        return
      }

      observe(event.lastEventId)

      if (terminal) {
        // Closed before the payload is parsed or the handler is invoked, not after: the terminal
        // event is this stream's last regardless of whether `JSON.parse` throws on a malformed
        // payload or the handler itself throws, and closing here is what stops the browser's
        // EventSource from reconnecting to a Turn with nothing left to say.
        closed = true
        source.close()
      }

      handler?.(JSON.parse(event.data) as T)
    })
  }

  on<TurnAcceptedEvent>('accepted', handlers.onAccepted)
  on<TurnProcessingEvent>('processing', handlers.onProcessing)
  on<TurnResponsePartEvent>('part', handlers.onPart)
  on<TurnStreamOutcomeEvent>('outcome', handlers.onOutcome, true)

  source.onerror = () => {
    if (closed) {
      return
    }

    if (source.readyState === EVENT_SOURCE_CLOSED) {
      // The browser has already given up - permanently, not just for this attempt - so the stream
      // really is over. Closing here is idempotent (the browser put it in this state already) but
      // guarantees the caller-visible `closed` flag agrees with reality regardless.
      closed = true
      source.close()
      handlers.onFailed?.()
      return
    }

    // Deliberately does not close: a transient failure is exactly what the browser's own
    // reconnect (using Last-Event-ID) is for, and closing here would defeat that reconnect.
    handlers.onDisconnected?.()
  }

  return {
    lastEventId: () => seen,
    close: () => {
      closed = true
      source.close()
    },
  }
}
