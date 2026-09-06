/**
 * Tests for BrowserVoiceTransport — the production WebRTC/data-channel transport.
 * Uses mocked browser APIs (RTCPeerConnection, getUserMedia, etc.) since jsdom
 * does not implement WebRTC. Every invariant pinned here must also hold for the contract.
 */
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import type { VoiceTransport, VoiceTransportCallbacks } from './voiceTransport'

// Mocking is deferred until after the implementation module exists.
// The module under test is imported dynamically within each test to avoid import-time failures.

// ── Mock infrastructure ───────────────────────────────────────────────────────

function makeCallbacks(overrides: Partial<VoiceTransportCallbacks> = {}): VoiceTransportCallbacks {
  return {
    onConnected: vi.fn(),
    onSpeechStarted: vi.fn(),
    onSpeechStopped: vi.fn(),
    onPartialTranscript: vi.fn(),
    onFinalTranscript: vi.fn(),
    onPlaybackStarted: vi.fn(),
    onPlaybackDone: vi.fn(),
    onPlaybackFailed: vi.fn(),
    onPlaybackIntegrityError: vi.fn(),
    onError: vi.fn(),
    onMicrophoneFailed: vi.fn(),
    ...overrides,
  }
}

class FakeMediaStreamTrack {
  enabled = true
  kind: string
  stop = vi.fn()
  constructor(kind = 'audio') {
    this.kind = kind
  }
}

class FakeMediaStream {
  private _tracks: FakeMediaStreamTrack[]
  constructor(tracks: FakeMediaStreamTrack[] = [new FakeMediaStreamTrack('audio')]) {
    this._tracks = tracks
  }
  getTracks() { return [...this._tracks] }
  getAudioTracks() { return this._tracks.filter(t => t.kind === 'audio') }
}

function makeFakeDataChannel() {
  const dc: Record<string, unknown> & { onmessage: ((e: { data: string }) => void) | null; onopen: (() => void) | null; onerror: ((e: unknown) => void) | null; close: ReturnType<typeof vi.fn>; send: ReturnType<typeof vi.fn>; readyState: string } = {
    onmessage: null,
    onopen: null,
    onerror: null,
    close: vi.fn(),
    send: vi.fn(),
    readyState: 'connecting',
    label: 'oai-events',
  }
  return dc
}

function makeFakePeerConnection() {
  const dataChannel = makeFakeDataChannel()
  const senders: { track: FakeMediaStreamTrack }[] = []

  const pc: Record<string, unknown> = {
    createDataChannel: vi.fn(() => dataChannel),
    addTrack: vi.fn((track: FakeMediaStreamTrack) => {
      const sender = { track }
      senders.push(sender)
      return sender
    }),
    createOffer: vi.fn(async () => ({ type: 'offer', sdp: 'v=0\r\noffer\r\n' })),
    setLocalDescription: vi.fn(async () => {}),
    setRemoteDescription: vi.fn(async () => {}),
    close: vi.fn(),
    getSenders: vi.fn(() => [...senders]),
    localDescription: { type: 'offer', sdp: 'v=0\r\noffer\r\n' },
    iceGatheringState: 'complete',
    connectionState: 'new',
    onicecandidate: null as ((e: { candidate: unknown }) => void) | null,
    onicegatheringstatechange: null as (() => void) | null,
    onconnectionstatechange: null as (() => void) | null,
    ontrack: null as ((e: { streams: unknown[] }) => void) | null,
    _dataChannel: dataChannel,
    _senders: senders,
  }
  return pc
}

let mockPeerConnection: ReturnType<typeof makeFakePeerConnection>
let mockStream: FakeMediaStream

beforeEach(() => {
  mockPeerConnection = makeFakePeerConnection()
  mockStream = new FakeMediaStream()

  // Regular functions (not arrows) so `new RTCPeerConnection()` works
  globalThis.RTCPeerConnection = function RTCPeerConnection() {
    return mockPeerConnection
  } as unknown as typeof globalThis.RTCPeerConnection

  globalThis.RTCSessionDescription = function RTCSessionDescription(desc: unknown) {
    return desc
  } as unknown as typeof globalThis.RTCSessionDescription

  const mediaDevices = {
    getUserMedia: vi.fn(async () => mockStream),
  }
  Object.defineProperty(globalThis, 'navigator', {
    value: { mediaDevices },
    writable: true,
    configurable: true,
  })
})

afterEach(() => {
  delete (globalThis as Record<string, unknown>)['RTCPeerConnection']
  delete (globalThis as Record<string, unknown>)['RTCSessionDescription']
  vi.restoreAllMocks()
})

// ── Tests ─────────────────────────────────────────────────────────────────────

describe('BrowserVoiceTransport', () => {
  async function loadModule() {
    const mod = await import('./browserVoiceTransport')
    return mod.BrowserVoiceTransport
  }

  // ── prepare ───────────────────────────────────────────────────────────────

  it('prepare acquires microphone and returns local SDP offer', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    const sdp = await transport.prepare()
    expect(navigator.mediaDevices.getUserMedia).toHaveBeenCalledWith({ audio: true })
    expect(sdp).toContain('v=0')
    expect(mockPeerConnection.createOffer).toHaveBeenCalled()
    expect(mockPeerConnection.setLocalDescription).toHaveBeenCalled()
  })

  it('prepare creates a data channel named "oai-events"', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    expect(mockPeerConnection.createDataChannel).toHaveBeenCalledWith('oai-events')
  })

  it('prepare adds audio track to peer connection', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    expect(mockPeerConnection.addTrack).toHaveBeenCalled()
  })

  it('prepare failure stops tracks and closes peer', async () => {
    const tracks = mockStream.getTracks()
    ;(mockPeerConnection.createOffer as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('offer failed'))

    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await expect(transport.prepare()).rejects.toThrow('offer failed')
    tracks.forEach(t => expect(t.stop).toHaveBeenCalled())
    expect(mockPeerConnection.close).toHaveBeenCalled()
  })

  it('prepare failure from getUserMedia does not leave resources open', async () => {
    ;(navigator.mediaDevices.getUserMedia as ReturnType<typeof vi.fn>).mockRejectedValue(
      new DOMException('NotAllowedError'),
    )

    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await expect(transport.prepare()).rejects.toThrow()
    // No peer connection should have been created
    expect(mockPeerConnection.close).not.toHaveBeenCalled()
  })

  // ── connect ───────────────────────────────────────────────────────────────

  it('connect sets remote SDP answer', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    transport.connect('v=0\r\nanswer\r\n', makeCallbacks(), 'vs-session-1')
    expect(mockPeerConnection.setRemoteDescription).toHaveBeenCalled()
  })

  it('connect requires prepare() first — throws if not prepared', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    expect(() => transport.connect('v=0\r\n', makeCallbacks(), 'vs-1')).toThrow()
  })

  it('connect stores voiceSessionId for nativeMessageId derivation', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\nanswer\r\n', callbacks, 'vs-session-42')

    // Simulate a final transcript event via data channel with item_id
    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    dc.onmessage?.({
      data: JSON.stringify({
        type: 'conversation.item.input_audio_transcription.completed',
        item_id: 'item_abc',
        transcript: 'hello world',
      }),
    })

    expect(callbacks.onFinalTranscript).toHaveBeenCalledWith(
      'hello world',
      'voice:vs-session-42:item_abc',
    )
  })

  // ── Data channel event parsing ────────────────────────────────────────────

  it('parses speech_started event', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')

    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    dc.onmessage?.({ data: JSON.stringify({ type: 'input_audio_buffer.speech_started' }) })
    expect(callbacks.onSpeechStarted).toHaveBeenCalledOnce()
  })

  it('parses speech_stopped event', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')

    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    dc.onmessage?.({ data: JSON.stringify({ type: 'input_audio_buffer.speech_stopped' }) })
    expect(callbacks.onSpeechStopped).toHaveBeenCalledOnce()
  })

  it('parses partial transcription', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')

    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    dc.onmessage?.({
      data: JSON.stringify({
        type: 'conversation.item.input_audio_transcription.delta',
        delta: 'add fi',
      }),
    })
    expect(callbacks.onPartialTranscript).toHaveBeenCalledWith('add fi')
  })

  it('parses playback started (response.audio.delta first arrival)', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')

    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    dc.onmessage?.({
      data: JSON.stringify({
        type: 'response.audio.delta',
        item_id: 'output_item_1',
        content_index: 0,
      }),
    })
    expect(callbacks.onPlaybackStarted).toHaveBeenCalledOnce()
  })

  it('parses playback done (response.audio.done)', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')

    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    dc.onmessage?.({ data: JSON.stringify({ type: 'response.audio.done' }) })
    expect(callbacks.onPlaybackDone).toHaveBeenCalledOnce()
  })

  it('parses playback integrity: fires error on transcript mismatch', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')

    // Speak canonical text to set the expected text
    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    dc.readyState = 'open'
    transport.speakCanonical('correct text')

    // Server returns different transcript
    dc.onmessage?.({
      data: JSON.stringify({
        type: 'response.audio_transcript.done',
        transcript: 'wrong text',
      }),
    })

    expect(callbacks.onPlaybackIntegrityError).toHaveBeenCalledWith('correct text', 'wrong text')
  })

  it('ignores malformed JSON on data channel without crashing', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')

    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    expect(() => dc.onmessage?.({ data: 'not json {{{' })).not.toThrow()
    expect(callbacks.onError).not.toHaveBeenCalled()
  })

  it('ignores unknown event types without crashing', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')

    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    expect(() => dc.onmessage?.({ data: JSON.stringify({ type: 'future.unknown.event' }) })).not.toThrow()
  })

  // ── setMuted ──────────────────────────────────────────────────────────────

  it('setMuted toggles audio track enabled state', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    transport.connect('v=0\r\n', makeCallbacks(), 'vs-1')

    transport.setMuted(true)
    const track = mockStream.getAudioTracks()[0]
    expect(track.enabled).toBe(false)

    transport.setMuted(false)
    expect(track.enabled).toBe(true)
  })

  // ── cancelPlayback ────────────────────────────────────────────────────────

  it('cancelPlayback sends response.cancel then conversation.item.truncate on data channel', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')

    // Need a tracked output item_id first
    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    dc.readyState = 'open'
    dc.onmessage?.({
      data: JSON.stringify({ type: 'response.audio.delta', item_id: 'output_1', content_index: 0 }),
    })

    transport.cancelPlayback(1500)

    const calls = dc.send.mock.calls.map((c: [string]) => JSON.parse(c[0]))
    const cancelMsg = calls.find((c: { type: string }) => c.type === 'response.cancel')
    const truncateMsg = calls.find((c: { type: string }) => c.type === 'conversation.item.truncate')

    expect(cancelMsg).toBeDefined()
    expect(truncateMsg).toMatchObject({
      type: 'conversation.item.truncate',
      item_id: 'output_1',
      content_index: 0,
      audio_end_ms: 1500,
    })
  })

  it('cancelPlayback throws if not connected', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    expect(() => transport.cancelPlayback(100)).toThrow('cancelPlayback requires connected transport')
  })

  it('cancelPlayback throws RangeError for negative duration when connected', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    transport.connect('v=0\r\n', makeCallbacks(), 'vs-1')
    expect(() => transport.cancelPlayback(-1)).toThrow(RangeError)
  })

  it('cancelPlayback throws RangeError for NaN when connected', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    transport.connect('v=0\r\n', makeCallbacks(), 'vs-1')
    expect(() => transport.cancelPlayback(NaN)).toThrow(RangeError)
  })

  // ── speakCanonical ────────────────────────────────────────────────────────

  it('speakCanonical sends response.create with pre_generated_assistant_message', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    transport.connect('v=0\r\n', makeCallbacks(), 'vs-1')

    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    dc.readyState = 'open'
    transport.speakCanonical('5 boxes of Steel Bolts added.')

    const calls = dc.send.mock.calls.map((c: [string]) => JSON.parse(c[0]))
    const createMsg = calls.find((c: { type: string }) => c.type === 'response.create')
    expect(createMsg).toBeDefined()
    expect(createMsg.response.pre_generated_assistant_message).toBe('5 boxes of Steel Bolts added.')
  })

  it('speakCanonical throws if not connected', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    expect(() => transport.speakCanonical('text')).toThrow('speakCanonical requires connected transport')
  })

  // ── disconnect ────────────────────────────────────────────────────────────

  it('disconnect closes data channel, peer connection, and stops tracks', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    transport.connect('v=0\r\n', makeCallbacks(), 'vs-1')
    transport.disconnect()

    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    expect(dc.close).toHaveBeenCalled()
    expect(mockPeerConnection.close).toHaveBeenCalled()
    mockStream.getTracks().forEach(t => expect(t.stop).toHaveBeenCalled())
  })

  it('disconnect prevents subsequent callbacks', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')
    transport.disconnect()

    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    dc.onmessage?.({ data: JSON.stringify({ type: 'input_audio_buffer.speech_started' }) })
    expect(callbacks.onSpeechStarted).not.toHaveBeenCalled()
  })

  it('disconnect is idempotent — second call is no-op', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    transport.connect('v=0\r\n', makeCallbacks(), 'vs-1')
    transport.disconnect()
    transport.disconnect()
    expect(mockPeerConnection.close).toHaveBeenCalledTimes(1)
  })

  // ── BrowserVoiceTransport satisfies VoiceTransport ────────────────────────

  it('BrowserVoiceTransport is assignable to VoiceTransport', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport: VoiceTransport = new BrowserVoiceTransport()
    expect(transport).toBeDefined()
  })

  // ── connection state events ───────────────────────────────────────────────

  it('fires onConnected when data channel opens', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')

    const dc = mockPeerConnection._dataChannel as ReturnType<typeof makeFakeDataChannel>
    dc.readyState = 'open'
    dc.onopen?.()

    expect(callbacks.onConnected).toHaveBeenCalledOnce()
  })

  it('fires onError when peer connection fails', async () => {
    const BrowserVoiceTransport = await loadModule()
    const transport = new BrowserVoiceTransport()
    await transport.prepare()
    const callbacks = makeCallbacks()
    transport.connect('v=0\r\n', callbacks, 'vs-1')

    mockPeerConnection.connectionState = 'failed'
    ;(mockPeerConnection.onconnectionstatechange as (() => void) | null)?.()

    expect(callbacks.onError).toHaveBeenCalledWith(expect.stringContaining('failed'))
  })

  // ── Never embeds Azure endpoint/token ─────────────────────────────────────

  it('does not embed any Azure endpoint or token in its source', async () => {
    const { BrowserVoiceTransport: Cls } = await import('./browserVoiceTransport')
    const source = Cls.toString()
    expect(source).not.toMatch(/azure|openai\.com|api-key/i)
  })
})
