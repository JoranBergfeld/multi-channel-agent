import { describe, expect, it, vi } from 'vitest'

import { recordingEventStreamFactory } from './testing/fakeEventSource'
import { TURN_EVENT_SEQUENCE, openTurnStream } from './turnStream'
import type { TurnStreamOutcomeEvent } from './turnStream'

describe('openTurnStream', () => {
  it('opens the per-Turn stream endpoint with no resume point on a first connection', () => {
    const { opened, factory } = recordingEventStreamFactory()

    openTurnStream({ turnId: 'turn-1', factory })

    expect(opened).toHaveLength(1)
    expect(opened[0]?.url).toBe('/api/turns/turn-1/events')
  })

  it('resumes from a caller-supplied lastEventId via the query parameter', () => {
    const { opened, factory } = recordingEventStreamFactory()

    openTurnStream({ turnId: 'turn-1', lastEventId: TURN_EVENT_SEQUENCE.processing, factory })

    expect(opened[0]?.url).toBe(`/api/turns/turn-1/events?lastEventId=${TURN_EVENT_SEQUENCE.processing}`)
  })

  it('dispatches each typed event to its own handler, retaining a data part payload and observing the outcome code', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onAccepted = vi.fn()
    const onProcessing = vi.fn()
    const onPart = vi.fn()
    const onOutcome = vi.fn()

    openTurnStream({
      turnId: 'turn-1',
      factory,
      handlers: { onAccepted, onProcessing, onPart, onOutcome },
    })

    const source = opened[0]!
    source.emit('accepted', { turnId: 'turn-1', receivedAt: '2026-09-05T00:00:00Z' }, String(TURN_EVENT_SEQUENCE.accepted))
    source.emit('processing', { turnId: 'turn-1', startedAt: '2026-09-05T00:00:01Z' }, String(TURN_EVENT_SEQUENCE.processing))
    source.emit(
      'part',
      { turnId: 'turn-1', order: 1, kind: 'text', text: 'Added 2 kg of flour.', payload: null },
      String(TURN_EVENT_SEQUENCE.firstPart),
    )
    const dataPayload = { version: 1, kind: 'stock_mutation', operation: 'add' }
    source.emit(
      'part',
      { turnId: 'turn-1', order: 2, kind: 'data', text: null, payload: dataPayload },
      String(TURN_EVENT_SEQUENCE.firstPart + 1),
    )
    source.emit(
      'outcome',
      {
        turnId: 'turn-1',
        status: 'completed',
        category: 'completed',
        code: 'stock_mutation_applied',
        summary: 'Added 2 kg of flour.',
        deliveries: [],
      } satisfies TurnStreamOutcomeEvent,
      String(TURN_EVENT_SEQUENCE.outcome),
    )

    expect(onAccepted).toHaveBeenCalledWith({ turnId: 'turn-1', receivedAt: '2026-09-05T00:00:00Z' })
    expect(onProcessing).toHaveBeenCalledWith({ turnId: 'turn-1', startedAt: '2026-09-05T00:00:01Z' })
    expect(onPart).toHaveBeenCalledTimes(2)
    expect(onPart).toHaveBeenNthCalledWith(2, { turnId: 'turn-1', order: 2, kind: 'data', text: null, payload: dataPayload })
    expect(onOutcome).toHaveBeenCalledWith(
      expect.objectContaining({ code: 'stock_mutation_applied', deliveries: [] }),
    )
  })

  it('closes the source itself the instant the terminal outcome event arrives', () => {
    const { opened, factory } = recordingEventStreamFactory()

    openTurnStream({ turnId: 'turn-1', factory })

    const source = opened[0]!
    expect(source.closed).toBe(false)

    source.emit(
      'outcome',
      { turnId: 'turn-1', status: 'completed', category: 'completed', code: 'ok', summary: '', deliveries: [] },
      String(TURN_EVENT_SEQUENCE.outcome),
    )

    expect(source.closed).toBe(true)
  })

  it('advances lastEventId() to the greatest valid event identity received so far', () => {
    const { opened, factory } = recordingEventStreamFactory()

    const stream = openTurnStream({ turnId: 'turn-1', factory })
    expect(stream.lastEventId()).toBe(0)

    const source = opened[0]!
    source.emit('accepted', { turnId: 'turn-1', receivedAt: '2026-09-05T00:00:00Z' }, String(TURN_EVENT_SEQUENCE.accepted))
    expect(stream.lastEventId()).toBe(TURN_EVENT_SEQUENCE.accepted)

    source.emit('processing', { turnId: 'turn-1', startedAt: '2026-09-05T00:00:01Z' }, String(TURN_EVENT_SEQUENCE.processing))
    expect(stream.lastEventId()).toBe(TURN_EVENT_SEQUENCE.processing)
  })

  it('reports a connection failure through onDisconnected exactly once without closing, so the browser can reconnect on its own', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onDisconnected = vi.fn()
    const onFailed = vi.fn()

    openTurnStream({ turnId: 'turn-1', factory, handlers: { onDisconnected, onFailed } })

    const source = opened[0]!
    source.fail()

    expect(onDisconnected).toHaveBeenCalledTimes(1)
    expect(onFailed).not.toHaveBeenCalled()
    expect(source.closed).toBe(false)
  })

  it('reports a permanent connection failure through onFailed exactly once, closing the source and suppressing onDisconnected', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onDisconnected = vi.fn()
    const onFailed = vi.fn()

    openTurnStream({ turnId: 'turn-1', factory, handlers: { onDisconnected, onFailed } })

    const source = opened[0]!
    source.failFatally()

    expect(onFailed).toHaveBeenCalledTimes(1)
    expect(onDisconnected).not.toHaveBeenCalled()
    expect(source.closed).toBe(true)
  })

  it('closes the source before invoking the terminal handler, so a throwing onOutcome still leaves the stream closed', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const boom = new Error('boom')
    const onOutcome = vi.fn(() => {
      throw boom
    })

    openTurnStream({ turnId: 'turn-1', factory, handlers: { onOutcome } })

    const source = opened[0]!

    expect(() =>
      source.emit(
        'outcome',
        { turnId: 'turn-1', status: 'completed', category: 'completed', code: 'ok', summary: '', deliveries: [] },
        String(TURN_EVENT_SEQUENCE.outcome),
      ),
    ).toThrow(boom)

    expect(source.closed).toBe(true)
  })

  it('rejects a stale, non-numeric, non-integer, or unsafe-integer event id, never regressing or corrupting lastEventId()', () => {
    const { opened, factory } = recordingEventStreamFactory()

    const stream = openTurnStream({ turnId: 'turn-1', factory })
    const source = opened[0]!

    source.emit('part', { turnId: 'turn-1', order: 1, kind: 'text', text: 'seed', payload: null }, '100')
    expect(stream.lastEventId()).toBe(100)

    const rejectedIds = ['2', 'not-a-number', '2.5', String(Number.MAX_SAFE_INTEGER + 2)]
    for (const rawId of rejectedIds) {
      source.emit('part', { turnId: 'turn-1', order: 2, kind: 'text', text: 'noise', payload: null }, rawId)
      expect(stream.lastEventId()).toBe(100)
    }
  })

  it('suppresses events observed after the caller closes the stream, and closes the underlying source', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onAccepted = vi.fn()

    const stream = openTurnStream({ turnId: 'turn-1', factory, handlers: { onAccepted } })

    const source = opened[0]!
    stream.close()
    expect(source.closed).toBe(true)

    source.emit('accepted', { turnId: 'turn-1', receivedAt: '2026-09-05T00:00:00Z' }, String(TURN_EVENT_SEQUENCE.accepted))

    expect(onAccepted).not.toHaveBeenCalled()
    expect(stream.lastEventId()).toBe(0)
  })
})
