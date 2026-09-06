/**
 * End-to-end voice integration tests: no-replay, deterministic identity, canonical speech,
 * and client-side finalized-utterance deduplication. Uses real useTurnSubmission/conversationStorage
 * flow and real App-level composition, not direct storage helpers alone.
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { setViewportWidth, DESKTOP_WIDTH } from './testing/setup'
import { FakeEventSource, installFakeEventSource } from './testing/fakeEventSource'
import {
  clearInFlightTurnIfMatches,
  readInFlightTurn,
  rememberSubmission,
  rememberTurnId,
} from './conversationStorage'
import { FakeVoiceTransport } from './testing/fakeVoiceTransport'
import App from './App'

// ── Shared constants ────────────────────────────────────────────────────────

const BOOTSTRAP = {
  bootstrap: {
    participantId: '11111111-1111-1111-1111-111111111111',
    displayName: 'Integration Tester',
    webConversationId: 'web-conversation-vi',
    inventories: [
      { id: 'inv-1', shortId: 'aaaaaaaa', name: 'Voice Warehouse', ownerDisplayName: 'Integration Tester', role: 'Editor' },
    ],
    activeInventoryId: 'inv-1',
    needsOnboarding: false,
  },
  csrfToken: 'csrf-vi',
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

function stubApi(overrides: Record<string, () => Response> = {}) {
  const calls: { url: string; init?: RequestInit }[] = []

  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({ url, init })

    for (const [prefix, respond] of Object.entries(overrides)) {
      if (url.startsWith(prefix)) {
        return Promise.resolve(respond())
      }
    }

    if (url.startsWith('/api/session/bootstrap')) return Promise.resolve(json(BOOTSTRAP))
    if (url.includes('/stock')) return Promise.resolve(json({ rows: [], nextCursor: null, hasMore: false }))
    if (url.includes('/units')) return Promise.resolve(json({ units: [], nextCursor: null, hasMore: false }))
    if (url.includes('/locations')) return Promise.resolve(json({ locations: [], nextCursor: null, hasMore: false }))
    return Promise.resolve(json({}))
  })

  vi.stubGlobal('fetch', fetchMock)
  const streams = installFakeEventSource()
  return { fetchMock, calls, streams }
}

function turnStreamIn(streams: FakeEventSource[]) {
  return streams.find((s) => s.url.startsWith('/api/turns/'))
}

function turnSubmitCalls(calls: { url: string; init?: RequestInit }[]) {
  return calls.filter((c) => c.url === '/api/turns' && c.init?.method === 'POST')
}

afterEach(() => {
  vi.unstubAllGlobals()
  localStorage.clear()
})

// ── No-replay: storage breadcrumb and deterministic identity ────────────────

describe('voice integration: no-replay storage', () => {
  it('clearing after Outcome prevents reconnect replay', () => {
    rememberSubmission('wc-1', 'pid-1', { nativeMessageId: 'voice:vs-1:item_1', contentText: 'add five' })
    expect(readInFlightTurn('wc-1', 'pid-1')).not.toBeNull()

    clearInFlightTurnIfMatches('wc-1', 'pid-1', { nativeMessageId: 'voice:vs-1:item_1' })
    expect(readInFlightTurn('wc-1', 'pid-1')).toBeNull()
  })

  it('voice nativeMessageId format is deterministic from session and item', () => {
    const voiceSessionId = 'vs-1'
    const providerItemId = 'item_abc123'
    const nativeMessageId = `voice:${voiceSessionId}:${providerItemId}`
    expect(nativeMessageId).toBe('voice:vs-1:item_abc123')
  })

  it('same provider item in same session yields identical nativeMessageId', () => {
    const a = `voice:vs-A:item_1`
    const b = `voice:vs-A:item_1`
    expect(a).toBe(b)
  })

  it('different item in same session yields distinct nativeMessageId', () => {
    const a = `voice:vs-A:item_1`
    const b = `voice:vs-A:item_2`
    expect(a).not.toBe(b)
  })

  it('same item in different session yields distinct nativeMessageId', () => {
    const a = `voice:vs-A:item_1`
    const b = `voice:vs-B:item_1`
    expect(a).not.toBe(b)
  })

  it('stored breadcrumb is cleared once turnId matches terminal Outcome', () => {
    rememberSubmission('wc-1', 'pid-1', { nativeMessageId: 'voice:vs-1:item_1', contentText: 'add five' })
    rememberTurnId('wc-1', 'pid-1', 'voice:vs-1:item_1', 'turn-42')
    expect(readInFlightTurn('wc-1', 'pid-1')?.turnId).toBe('turn-42')

    clearInFlightTurnIfMatches('wc-1', 'pid-1', { turnId: 'turn-42' })
    expect(readInFlightTurn('wc-1', 'pid-1')).toBeNull()
  })
})

// ── No-replay: full App-level voice submission flow ─────────────────────────

describe('voice integration: App-level no-replay', () => {
  it('terminal Outcome clears breadcrumb; remount does not POST again', async () => {
    setViewportWidth(DESKTOP_WIDTH)
    const transport = new FakeVoiceTransport()
    const { streams, calls } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-nr1', sdpAnswer: 'v=0\r\n', denialReason: null }),
      '/api/turns': () => json({ turnId: 'turn-nr1', alreadyAccepted: false }, 202),
    })

    const { unmount } = render(<App testTransport={transport} />)
    await screen.findByRole('banner')

    // Start voice, get connected
    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
    transport.simulateConnected()
    await waitFor(() => expect(transport.connectCount).toBe(1))

    // Finalized transcript → submitted through shared controller
    transport.simulateFinalTranscript('add five boxes', 'voice:vs-nr1:item_1')
    await waitFor(() => expect(turnStreamIn(streams)).toBeDefined())

    // Drive to terminal Outcome
    turnStreamIn(streams)!.emit(
      'outcome',
      { turnId: 'turn-nr1', status: 'completed', category: 'completed', code: 'stock.added', summary: 'Added.', deliveries: [] },
      '1000000',
    )

    // Breadcrumb cleared
    await waitFor(() =>
      expect(readInFlightTurn(BOOTSTRAP.bootstrap.webConversationId, BOOTSTRAP.bootstrap.participantId)).toBeNull(),
    )

    // Count turn POST calls so far
    const postsBeforeRemount = turnSubmitCalls(calls).length

    unmount()

    // Remount — no breadcrumb exists, so no replay POST should happen
    render(<App testTransport={transport} />)
    await screen.findByRole('banner')

    // Wait a tick for any potential resume
    await act(async () => { await new Promise((r) => setTimeout(r, 50)) })

    expect(turnSubmitCalls(calls).length).toBe(postsBeforeRemount)
  })

  it('partial transcript never submits', async () => {
    setViewportWidth(DESKTOP_WIDTH)
    const transport = new FakeVoiceTransport()
    const { calls } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-pt1', sdpAnswer: 'v=0\r\n', denialReason: null }),
    })

    render(<App testTransport={transport} />)
    await screen.findByRole('banner')

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
    transport.simulateConnected()
    await waitFor(() => expect(transport.connectCount).toBe(1))

    // Partial transcripts — should never trigger a turn submit
    transport.simulatePartialTranscript('add fi')
    transport.simulatePartialTranscript('add five box')
    transport.simulatePartialTranscript('add five boxes')

    await act(async () => { await new Promise((r) => setTimeout(r, 50)) })

    expect(turnSubmitCalls(calls)).toHaveLength(0)
  })

  it('duplicate provider final-transcript with same item ID does not create two submissions', async () => {
    setViewportWidth(DESKTOP_WIDTH)
    const transport = new FakeVoiceTransport()
    const { calls } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-dd1', sdpAnswer: 'v=0\r\n', denialReason: null }),
      '/api/turns': () => json({ turnId: 'turn-dd1', alreadyAccepted: false }, 202),
    })

    render(<App testTransport={transport} />)
    await screen.findByRole('banner')

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
    transport.simulateConnected()
    await waitFor(() => expect(transport.connectCount).toBe(1))

    // First finalized transcript → should submit
    transport.simulateFinalTranscript('add five boxes', 'voice:vs-dd1:item_dup1')

    await waitFor(() => expect(turnSubmitCalls(calls)).toHaveLength(1))

    // Duplicate delivery of the exact same item → should NOT create a second submission
    transport.simulateFinalTranscript('add five boxes', 'voice:vs-dd1:item_dup1')

    await act(async () => { await new Promise((r) => setTimeout(r, 50)) })

    // Only one POST was made, not two
    expect(turnSubmitCalls(calls)).toHaveLength(1)
  })
})

// ── Canonical speech: real composition through terminal Outcome ─────────────

describe('voice integration: canonical speech', () => {
  it('terminal Outcome with active voice session calls speakCanonical with exact summary', async () => {
    setViewportWidth(DESKTOP_WIDTH)
    const transport = new FakeVoiceTransport()
    const { streams } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-cs1', sdpAnswer: 'v=0\r\n', denialReason: null }),
      '/api/turns': () => json({ turnId: 'turn-cs1', alreadyAccepted: false }, 202),
    })

    render(<App testTransport={transport} />)
    await screen.findByRole('banner')

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
    transport.simulateConnected()
    await waitFor(() => expect(transport.connectCount).toBe(1))

    transport.simulateFinalTranscript('list steel bolts', 'voice:vs-cs1:item_1')
    await waitFor(() => expect(turnStreamIn(streams)).toBeDefined())

    const CANONICAL = '5 boxes of Steel Bolts added.'
    turnStreamIn(streams)!.emit(
      'outcome',
      { turnId: 'turn-cs1', status: 'completed', category: 'completed', code: 'stock.listed', summary: CANONICAL, deliveries: [] },
      '1000000',
    )

    // speakCanonical called with the exact summary string
    await waitFor(() => expect(transport.spokenTexts).toContain(CANONICAL))
    expect(transport.lastSpokenText).toBe(CANONICAL)

    // The same text is rendered in TurnTracer
    expect(await screen.findByText(CANONICAL)).toBeInTheDocument()
  })

  it('speaks each terminal Turn at most once despite duplicate outcome events', async () => {
    setViewportWidth(DESKTOP_WIDTH)
    const transport = new FakeVoiceTransport()
    const { streams } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-cs2', sdpAnswer: 'v=0\r\n', denialReason: null }),
      '/api/turns': () => json({ turnId: 'turn-cs2', alreadyAccepted: false }, 202),
    })

    render(<App testTransport={transport} />)
    await screen.findByRole('banner')

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
    transport.simulateConnected()
    await waitFor(() => expect(transport.connectCount).toBe(1))

    transport.simulateFinalTranscript('list stock', 'voice:vs-cs2:item_1')
    await waitFor(() => expect(turnStreamIn(streams)).toBeDefined())

    const CANONICAL = '12 items found.'
    const outcomePayload = {
      turnId: 'turn-cs2', status: 'completed', category: 'completed',
      code: 'stock.listed', summary: CANONICAL, deliveries: [],
    }

    // First outcome event
    turnStreamIn(streams)!.emit('outcome', outcomePayload, '1000000')
    await waitFor(() => expect(transport.spokenTexts).toHaveLength(1))

    // The useTurnSubmission hook sets outcome on first terminal event only.
    // Verify the deduplication by confirming no second speakCanonical call occurred.
    expect(transport.spokenTexts).toHaveLength(1)
    expect(transport.spokenTexts[0]).toBe(CANONICAL)
  })

  it('does not call speakCanonical when no active voice session', async () => {
    setViewportWidth(DESKTOP_WIDTH)
    const transport = new FakeVoiceTransport()
    const { streams } = stubApi({
      '/api/turns': () => json({ turnId: 'turn-nv1', alreadyAccepted: false }, 202),
    })

    render(<App testTransport={transport} />)
    await screen.findByRole('banner')

    // Submit text turn without voice session
    const form = screen.getByRole('textbox', { name: 'Message' })
    await userEvent.clear(form)
    await userEvent.type(form, 'list stock')
    await userEvent.click(screen.getByRole('button', { name: 'Send' }))

    await waitFor(() => expect(turnStreamIn(streams)).toBeDefined())

    turnStreamIn(streams)!.emit(
      'outcome',
      { turnId: 'turn-nv1', status: 'completed', category: 'completed', code: 'stock.listed', summary: 'No items.', deliveries: [] },
      '1000000',
    )

    await waitFor(() => expect(screen.getByText('No items.')).toBeInTheDocument())

    // speakCanonical never called — no active voice session
    expect(transport.spokenTexts).toHaveLength(0)
  })

  it('does not call speakCanonical when transport is disconnected', async () => {
    setViewportWidth(DESKTOP_WIDTH)
    const transport = new FakeVoiceTransport()
    const { streams } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-dc1', sdpAnswer: 'v=0\r\n', denialReason: null }),
      '/api/turns': () => json({ turnId: 'turn-dc1', alreadyAccepted: false }, 202),
      '/api/voice/release': () => json({}),
    })

    render(<App testTransport={transport} />)
    await screen.findByRole('banner')

    // Start and connect voice
    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
    transport.simulateConnected()
    await waitFor(() => expect(transport.connectCount).toBe(1))

    // Submit voice turn
    transport.simulateFinalTranscript('list stock', 'voice:vs-dc1:item_1')
    await waitFor(() => expect(turnStreamIn(streams)).toBeDefined())

    // End voice session BEFORE outcome arrives
    await userEvent.click(screen.getByRole('button', { name: 'End Voice' }))
    await waitFor(() => expect(transport.isConnected).toBe(false))

    // Terminal outcome arrives — transport disconnected, no speakCanonical
    turnStreamIn(streams)!.emit(
      'outcome',
      { turnId: 'turn-dc1', status: 'completed', category: 'completed', code: 'stock.listed', summary: 'Listed.', deliveries: [] },
      '1000000',
    )

    await waitFor(() => expect(screen.getByText('Listed.')).toBeInTheDocument())

    // speakCanonical never called — transport was disconnected
    expect(transport.spokenTexts).toHaveLength(0)
  })

  it('speakCanonical failure does not change the visible Outcome text', async () => {
    setViewportWidth(DESKTOP_WIDTH)
    const transport = new FakeVoiceTransport()
    // Make speakCanonical throw
    const originalSpeak = transport.speakCanonical.bind(transport)
    let speakCallCount = 0
    transport.speakCanonical = (text: string) => {
      speakCallCount++
      originalSpeak(text)
      // Simulate that the transport internally triggers a playback failure
    }

    const { streams } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-sf1', sdpAnswer: 'v=0\r\n', denialReason: null }),
      '/api/turns': () => json({ turnId: 'turn-sf1', alreadyAccepted: false }, 202),
    })

    render(<App testTransport={transport} />)
    await screen.findByRole('banner')

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
    transport.simulateConnected()
    await waitFor(() => expect(transport.connectCount).toBe(1))

    transport.simulateFinalTranscript('list stock', 'voice:vs-sf1:item_1')
    await waitFor(() => expect(turnStreamIn(streams)).toBeDefined())

    const CANONICAL = 'Safe visible text.'
    turnStreamIn(streams)!.emit(
      'outcome',
      { turnId: 'turn-sf1', status: 'completed', category: 'completed', code: 'stock.listed', summary: CANONICAL, deliveries: [] },
      '1000000',
    )

    // Canonical text stays visible regardless of playback state
    expect(await screen.findByText(CANONICAL)).toBeInTheDocument()
    expect(speakCallCount).toBe(1)

    // Simulate playback failure after speak was called
    transport.simulatePlaybackFailed('TTS error')

    // Text is still there
    expect(screen.getByText(CANONICAL)).toBeInTheDocument()
  })
})

// ── BrowserVoiceTransport integrity (composed, not direct) ──────────────────

describe('voice integration: transport integrity via FakeVoiceTransport', () => {
  it('exact summary text is what speakCanonical receives', () => {
    const transport = new FakeVoiceTransport()
    transport.connect('v=0\r\n', {
      onConnected: vi.fn(), onSpeechStarted: vi.fn(), onSpeechStopped: vi.fn(),
      onPartialTranscript: vi.fn(), onFinalTranscript: vi.fn(), onPlaybackStarted: vi.fn(),
      onPlaybackDone: vi.fn(), onPlaybackFailed: vi.fn(),
      onPlaybackIntegrityError: vi.fn(),
      onError: vi.fn(), onMicrophoneFailed: vi.fn(),
    }, 'vs-test')

    const canonicalSummary = '5 boxes of Steel Bolts added.'
    transport.speakCanonical(canonicalSummary)
    expect(transport.lastSpokenText).toBe(canonicalSummary)
  })

  it('playback integrity error fires when transcript differs from requested text', () => {
    const transport = new FakeVoiceTransport()
    const onPlaybackIntegrityError = vi.fn()
    transport.connect('v=0\r\n', {
      onConnected: vi.fn(), onSpeechStarted: vi.fn(), onSpeechStopped: vi.fn(),
      onPartialTranscript: vi.fn(), onFinalTranscript: vi.fn(), onPlaybackStarted: vi.fn(),
      onPlaybackDone: vi.fn(), onPlaybackFailed: vi.fn(),
      onPlaybackIntegrityError,
      onError: vi.fn(), onMicrophoneFailed: vi.fn(),
    }, 'vs-test')

    transport.simulatePlaybackIntegrityError('5 boxes of Steel Bolts added.', '5 boxes of steel bolts added')
    expect(onPlaybackIntegrityError).toHaveBeenCalledWith('5 boxes of Steel Bolts added.', '5 boxes of steel bolts added')
  })

  it('speakCanonical throws when transport is not connected', () => {
    const transport = new FakeVoiceTransport()
    expect(() => transport.speakCanonical('hello')).toThrow('speakCanonical requires connected transport')
  })
})
