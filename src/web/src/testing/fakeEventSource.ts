// Faithful enough to swap in for the browser's global `EventSource` in tests: components under
// test call `new EventSource(url)`, `addEventListener`, and `close()` exactly as they would against
// the real thing, and the test drives the stream forward with `emit`/`fail` instead of a real HTTP
// response. Deliberately imports no production-code types - it stands in for a Web Platform API,
// not for anything this application defines, so it must never drift out of sync with either.
import { vi } from 'vitest'

type SseListener = (event: MessageEvent<string>) => void

/** A minimal, structurally-compatible stand-in for the browser's `EventSource`. */
export class FakeEventSource {
  readonly url: string
  closed = false
  onerror: ((event: Event) => void) | null = null

  private readonly listeners = new Map<string, Set<SseListener>>()

  constructor(url: string) {
    this.url = url
  }

  addEventListener(type: string, listener: SseListener): void {
    let forType = this.listeners.get(type)
    if (!forType) {
      forType = new Set()
      this.listeners.set(type, forType)
    }

    forType.add(listener)
  }

  removeEventListener(type: string, listener: SseListener): void {
    this.listeners.get(type)?.delete(listener)
  }

  close(): void {
    this.closed = true
  }

  /**
   * Delivers a named SSE event to every listener registered for it, JSON-encoding `data` exactly
   * as the real backend does (see `ServerSentEvents.WriteEventAsync`), so a test passes a plain
   * value and the component under test still receives a JSON string in `event.data`.
   */
  emit(type: string, data: unknown, lastEventId = ''): void {
    const event = new MessageEvent(type, { data: JSON.stringify(data), lastEventId })
    this.listeners.get(type)?.forEach((listener) => listener(event))
  }

  /**
   * Simulates the underlying connection failing - exactly how a real `EventSource` reports one:
   * through `onerror`, never by throwing. Does not close the stream by itself, since a real
   * `EventSource` reconnects on its own after a transient error unless the server ends the stream.
   */
  fail(): void {
    this.onerror?.(new Event('error'))
  }
}

/**
 * Builds an `EventSource`-shaped factory function that records every instance it creates, so a
 * test can reach into `opened` to drive (or inspect) whichever stream the component under test has
 * open - including after a reconnect, which opens a new instance rather than reusing the old one.
 */
export function recordingEventStreamFactory(): {
  opened: FakeEventSource[]
  factory: (url: string) => FakeEventSource
} {
  const opened: FakeEventSource[] = []

  function factory(url: string): FakeEventSource {
    const source = new FakeEventSource(url)
    opened.push(source)
    return source
  }

  return { opened, factory }
}

/**
 * Replaces the global `EventSource` constructor with a recording fake for the current test.
 * `vite.config.ts` enables Vitest's `unstubGlobals` option, so the stub is undone automatically
 * before the next test runs - a caller never has to restore it itself.
 */
export function installFakeEventSource(): { opened: FakeEventSource[] } {
  const { opened, factory } = recordingEventStreamFactory()
  vi.stubGlobal('EventSource', factory)
  return { opened }
}
