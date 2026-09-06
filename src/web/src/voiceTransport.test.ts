/**
 * Tests for the FakeVoiceTransport. The fake serves as the primary behavioural spec for
 * VoiceTransport: every invariant pinned here must also hold in the real adapter.
 */
import { describe, expect, it, vi } from 'vitest'
import { FakeVoiceTransport } from './testing/fakeVoiceTransport'
import type { VoiceTransport, VoiceTransportCallbacks } from './voiceTransport'

// ── Helpers ────────────────────────────────────────────────────────────────────

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

function connected(): [FakeVoiceTransport, VoiceTransportCallbacks] {
  const transport = new FakeVoiceTransport()
  const callbacks = makeCallbacks()
  transport.connect('v=0\r\n', callbacks, 'vs-test')
  return [transport, callbacks]
}

// ── Plan Tests (6 required) ───────────────────────────────────────────────────

describe('FakeVoiceTransport', () => {
  it('prepare returns a fake SDP offer', async () => {
    const transport = new FakeVoiceTransport()
    const offer = await transport.prepare()
    expect(offer).toContain('v=0')
  })

  it('connect dispatches connected callback', () => {
    const transport = new FakeVoiceTransport()
    const onConnected = vi.fn()
    transport.connect('v=0\r\n', makeCallbacks({ onConnected }), 'vs-test')
    transport.simulateConnected()
    expect(onConnected).toHaveBeenCalledOnce()
  })

  it('simulateFinalTranscript dispatches with text and nativeMessageId only', () => {
    const transport = new FakeVoiceTransport()
    const onFinalTranscript = vi.fn()
    transport.connect('v=0\r\n', makeCallbacks({ onFinalTranscript }), 'vs-test')
    transport.simulateFinalTranscript('add five', 'voice:vs-1:item_1')
    expect(onFinalTranscript).toHaveBeenCalledWith('add five', 'voice:vs-1:item_1')
  })

  it('cancelPlayback records the call for assertion', () => {
    const [transport] = connected()
    transport.cancelPlayback(1500)
    expect(transport.cancelPlaybackCalls).toEqual([1500])
  })

  it('speakCanonical records the spoken text', () => {
    const [transport] = connected()
    transport.speakCanonical('5 boxes of Steel Bolts added.')
    expect(transport.lastSpokenText).toBe('5 boxes of Steel Bolts added.')
  })

  it('disconnect prevents subsequent callbacks', () => {
    const [transport, callbacks] = connected()
    transport.disconnect()
    transport.simulateError('late error')
    expect(callbacks.onError).not.toHaveBeenCalled()
  })

  // ── Every callback ──────────────────────────────────────────────────────────

  it('simulateSpeechStarted fires onSpeechStarted', () => {
    const [transport, callbacks] = connected()
    transport.simulateSpeechStarted()
    expect(callbacks.onSpeechStarted).toHaveBeenCalledOnce()
  })

  it('simulateSpeechStopped fires onSpeechStopped', () => {
    const [transport, callbacks] = connected()
    transport.simulateSpeechStopped()
    expect(callbacks.onSpeechStopped).toHaveBeenCalledOnce()
  })

  it('simulatePartialTranscript fires onPartialTranscript with text', () => {
    const [transport, callbacks] = connected()
    transport.simulatePartialTranscript('add fi')
    expect(callbacks.onPartialTranscript).toHaveBeenCalledWith('add fi')
  })

  it('simulatePlaybackStarted fires onPlaybackStarted', () => {
    const [transport, callbacks] = connected()
    transport.simulatePlaybackStarted()
    expect(callbacks.onPlaybackStarted).toHaveBeenCalledOnce()
  })

  it('simulatePlaybackDone fires onPlaybackDone', () => {
    const [transport, callbacks] = connected()
    transport.simulatePlaybackDone()
    expect(callbacks.onPlaybackDone).toHaveBeenCalledOnce()
  })

  it('simulatePlaybackFailed fires onPlaybackFailed with error', () => {
    const [transport, callbacks] = connected()
    transport.simulatePlaybackFailed('network error')
    expect(callbacks.onPlaybackFailed).toHaveBeenCalledWith('network error')
  })

  it('simulatePlaybackIntegrityError fires onPlaybackIntegrityError with requested and received', () => {
    const [transport, callbacks] = connected()
    transport.simulatePlaybackIntegrityError('correct text', 'wrong text')
    expect(callbacks.onPlaybackIntegrityError).toHaveBeenCalledWith('correct text', 'wrong text')
  })

  it('simulateMicrophoneFailed fires onMicrophoneFailed with error', () => {
    const [transport, callbacks] = connected()
    transport.simulateMicrophoneFailed('Permission denied')
    expect(callbacks.onMicrophoneFailed).toHaveBeenCalledWith('Permission denied')
  })

  it('simulateError fires onError with error', () => {
    const [transport, callbacks] = connected()
    transport.simulateError('connection reset')
    expect(callbacks.onError).toHaveBeenCalledWith('connection reset')
  })

  // ── Final transcript exact payload — no uncertain field ────────────────────

  it('onFinalTranscript receives exactly text and nativeMessageId — no extra arguments', () => {
    const [transport, callbacks] = connected()
    transport.simulateFinalTranscript('order placed', 'voice:sess-42:item_99')
    const calls = (callbacks.onFinalTranscript as ReturnType<typeof vi.fn>).mock.calls
    expect(calls).toHaveLength(1)
    // Exactly two arguments — no uncertain/confidence/logprobs/provider fields
    expect(calls[0]).toHaveLength(2)
    expect(calls[0][0]).toBe('order placed')
    expect(calls[0][1]).toBe('voice:sess-42:item_99')
  })

  it('nativeMessageId follows voice:${voiceSessionId}:${itemId} format', () => {
    const [transport, callbacks] = connected()
    transport.simulateFinalTranscript('text', 'voice:vs-session-1:item_abc123')
    expect(callbacks.onFinalTranscript).toHaveBeenCalledWith('text', 'voice:vs-session-1:item_abc123')
  })

  // ── Reconnect ───────────────────────────────────────────────────────────────

  it('reconnect after disconnect starts fresh session with new callbacks', () => {
    const [transport, firstCallbacks] = connected()
    transport.simulateConnected()
    transport.disconnect()

    const secondCallbacks = makeCallbacks()
    transport.connect('v=0\r\nanswer2\r\n', secondCallbacks, 'vs-test')
    transport.simulateConnected()
    transport.simulateSpeechStarted()

    // Old callbacks are not fired
    expect(firstCallbacks.onConnected).toHaveBeenCalledOnce()
    expect(firstCallbacks.onSpeechStarted).not.toHaveBeenCalled()

    // New callbacks receive events
    expect(secondCallbacks.onConnected).toHaveBeenCalledOnce()
    expect(secondCallbacks.onSpeechStarted).toHaveBeenCalledOnce()
  })

  it('connect increments connectCount', () => {
    const transport = new FakeVoiceTransport()
    expect(transport.connectCount).toBe(0)
    transport.connect('v=0\r\n', makeCallbacks(), 'vs-test')
    expect(transport.connectCount).toBe(1)
    transport.disconnect()
    transport.connect('v=0\r\n', makeCallbacks(), 'vs-test')
    expect(transport.connectCount).toBe(2)
  })

  it('connect stores the sdpAnswer', () => {
    const transport = new FakeVoiceTransport()
    transport.connect('v=0\r\nsdp=answer\r\n', makeCallbacks(), 'vs-test')
    expect(transport.lastConnectSdpAnswer).toBe('v=0\r\nsdp=answer\r\n')
  })

  // ── Idempotent disconnect ───────────────────────────────────────────────────

  it('disconnect is idempotent — second call is a no-op', () => {
    const [transport] = connected()
    transport.disconnect()
    transport.disconnect()
    expect(transport.disconnectCount).toBe(1)
  })

  it('disconnect clears callback references', () => {
    const [transport] = connected()
    transport.disconnect()
    expect(transport.isConnected).toBe(false)
  })

  // ── Mute history ───────────────────────────────────────────────────────────

  it('setMuted records mute state', () => {
    const [transport] = connected()
    transport.setMuted(true)
    expect(transport.isMuted).toBe(true)
  })

  it('setMuted appends to muteHistory', () => {
    const [transport] = connected()
    transport.setMuted(true)
    transport.setMuted(false)
    transport.setMuted(true)
    expect(transport.muteHistory).toEqual([true, false, true])
  })

  it('setMuted can be called before connect', () => {
    const transport = new FakeVoiceTransport()
    transport.setMuted(true)
    expect(transport.isMuted).toBe(true)
    expect(transport.muteHistory).toEqual([true])
  })

  it('setMuted can be called after disconnect', () => {
    const [transport] = connected()
    transport.disconnect()
    transport.setMuted(false)
    expect(transport.muteHistory).toContain(false)
  })

  // ── Multiple canonical/cancel calls ────────────────────────────────────────

  it('speakCanonical accumulates spokenTexts in order', () => {
    const [transport] = connected()
    transport.speakCanonical('First message')
    transport.speakCanonical('Second message')
    expect(transport.spokenTexts).toEqual(['First message', 'Second message'])
    expect(transport.lastSpokenText).toBe('Second message')
  })

  it('cancelPlayback accumulates multiple calls', () => {
    const [transport] = connected()
    transport.cancelPlayback(0)
    transport.cancelPlayback(750)
    transport.cancelPlayback(1500)
    expect(transport.cancelPlaybackCalls).toEqual([0, 750, 1500])
  })

  // ── Invalid duration validation ────────────────────────────────────────────

  it('cancelPlayback throws on negative duration', () => {
    const [transport] = connected()
    expect(() => transport.cancelPlayback(-1)).toThrow()
  })

  it('cancelPlayback throws on NaN', () => {
    const [transport] = connected()
    expect(() => transport.cancelPlayback(NaN)).toThrow()
  })

  it('cancelPlayback throws on Infinity', () => {
    const [transport] = connected()
    expect(() => transport.cancelPlayback(Infinity)).toThrow()
  })

  it('cancelPlayback throws on -Infinity', () => {
    const [transport] = connected()
    expect(() => transport.cancelPlayback(-Infinity)).toThrow()
  })

  it('cancelPlayback accepts zero', () => {
    const [transport] = connected()
    expect(() => transport.cancelPlayback(0)).not.toThrow()
    expect(transport.cancelPlaybackCalls).toEqual([0])
  })

  // ── Connected-only commands: cancelPlayback and speakCanonical ────────────

  it('cancelPlayback throws InvalidState if not connected (before connect)', () => {
    const transport = new FakeVoiceTransport()
    expect(() => transport.cancelPlayback(100)).toThrow(
      'cancelPlayback requires connected transport',
    )
    expect(transport.cancelPlaybackCalls).toEqual([])
  })

  it('cancelPlayback throws InvalidState if not connected (after disconnect)', () => {
    const [transport] = connected()
    transport.disconnect()
    expect(() => transport.cancelPlayback(200)).toThrow(
      'cancelPlayback requires connected transport',
    )
    expect(transport.cancelPlaybackCalls).toEqual([])
  })

  it('cancelPlayback state guard fires before argument validation', () => {
    const transport = new FakeVoiceTransport()
    // disconnected: InvalidState error, not RangeError, even with invalid arg
    expect(() => transport.cancelPlayback(-1)).toThrow('cancelPlayback requires connected transport')
    expect(transport.cancelPlaybackCalls).toEqual([])
  })

  it('speakCanonical throws InvalidState if not connected (before connect)', () => {
    const transport = new FakeVoiceTransport()
    expect(() => transport.speakCanonical('early text')).toThrow(
      'speakCanonical requires connected transport',
    )
    expect(transport.spokenTexts).toEqual([])
  })

  it('speakCanonical throws InvalidState if not connected (after disconnect)', () => {
    const [transport] = connected()
    transport.disconnect()
    expect(() => transport.speakCanonical('post disconnect')).toThrow(
      'speakCanonical requires connected transport',
    )
    expect(transport.spokenTexts).toEqual([])
  })

  // ── Connect-over-connect (no intervening disconnect) ───────────────────────

  it('connect-over-connect discards old callbacks; only new callbacks receive events', () => {
    const transport = new FakeVoiceTransport()
    const firstCallbacks = makeCallbacks()
    transport.connect('v=0\r\nfirst\r\n', firstCallbacks, 'vs-test')
    transport.simulateConnected() // goes to firstCallbacks

    const secondCallbacks = makeCallbacks()
    transport.connect('v=0\r\nsecond\r\n', secondCallbacks, 'vs-test')
    transport.simulateConnected()
    transport.simulateSpeechStarted()

    // Old callbacks receive no events after re-connect
    expect(firstCallbacks.onConnected).toHaveBeenCalledOnce()
    expect(firstCallbacks.onSpeechStarted).not.toHaveBeenCalled()

    // New callbacks receive all subsequent events
    expect(secondCallbacks.onConnected).toHaveBeenCalledOnce()
    expect(secondCallbacks.onSpeechStarted).toHaveBeenCalledOnce()
  })

  it('connect-over-connect increments connectCount', () => {
    const transport = new FakeVoiceTransport()
    transport.connect('v=0\r\nfirst\r\n', makeCallbacks(), 'vs-test')
    transport.connect('v=0\r\nsecond\r\n', makeCallbacks(), 'vs-test')
    expect(transport.connectCount).toBe(2)
  })

  it('connect-over-connect does not increment disconnectCount', () => {
    const transport = new FakeVoiceTransport()
    transport.connect('v=0\r\nfirst\r\n', makeCallbacks(), 'vs-test')
    transport.connect('v=0\r\nsecond\r\n', makeCallbacks(), 'vs-test')
    expect(transport.disconnectCount).toBe(0)
  })

  // ── Simulate helpers do nothing before connect or after disconnect ──────────

  it('simulate helpers before connect do not throw', () => {
    const transport = new FakeVoiceTransport()
    expect(() => {
      transport.simulateConnected()
      transport.simulateSpeechStarted()
      transport.simulateSpeechStopped()
      transport.simulatePartialTranscript('x')
      transport.simulateFinalTranscript('x', 'voice:s:i')
      transport.simulatePlaybackStarted()
      transport.simulatePlaybackDone()
      transport.simulatePlaybackFailed('err')
      transport.simulatePlaybackIntegrityError('a', 'b')
      transport.simulateError('err')
      transport.simulateMicrophoneFailed('err')
    }).not.toThrow()
  })

  it('simulate helpers after disconnect do not fire callbacks', () => {
    const [transport, callbacks] = connected()
    transport.disconnect()
    transport.simulateConnected()
    transport.simulateSpeechStarted()
    transport.simulateSpeechStopped()
    transport.simulatePartialTranscript('late')
    transport.simulateFinalTranscript('late', 'voice:s:i')
    transport.simulatePlaybackStarted()
    transport.simulatePlaybackDone()
    transport.simulatePlaybackFailed('err')
    transport.simulatePlaybackIntegrityError('a', 'b')
    transport.simulateMicrophoneFailed('mic err')

    expect(callbacks.onConnected).not.toHaveBeenCalled()
    expect(callbacks.onSpeechStarted).not.toHaveBeenCalled()
    expect(callbacks.onSpeechStopped).not.toHaveBeenCalled()
    expect(callbacks.onPartialTranscript).not.toHaveBeenCalled()
    expect(callbacks.onFinalTranscript).not.toHaveBeenCalled()
    expect(callbacks.onPlaybackStarted).not.toHaveBeenCalled()
    expect(callbacks.onPlaybackDone).not.toHaveBeenCalled()
    expect(callbacks.onPlaybackFailed).not.toHaveBeenCalled()
    expect(callbacks.onPlaybackIntegrityError).not.toHaveBeenCalled()
    expect(callbacks.onMicrophoneFailed).not.toHaveBeenCalled()
  })

  // ── Source callback isolation ───────────────────────────────────────────────

  it('two independent transports do not share callbacks', () => {
    const transportA = new FakeVoiceTransport()
    const transportB = new FakeVoiceTransport()
    const cbA = makeCallbacks()
    const cbB = makeCallbacks()
    transportA.connect('v=0\r\n', cbA)
    transportB.connect('v=0\r\n', cbB)

    transportA.simulateConnected()
    transportA.simulateError('only A')

    expect(cbA.onConnected).toHaveBeenCalledOnce()
    expect(cbA.onError).toHaveBeenCalledWith('only A')
    expect(cbB.onConnected).not.toHaveBeenCalled()
    expect(cbB.onError).not.toHaveBeenCalled()
  })

  // ── FakeVoiceTransport satisfies VoiceTransport interface ──────────────────

  it('FakeVoiceTransport is assignable to VoiceTransport', () => {
    const transport: VoiceTransport = new FakeVoiceTransport()
    expect(transport).toBeDefined()
  })

  // ── disconnectCount tracks disconnect calls ────────────────────────────────

  it('disconnectCount is 0 before any disconnect', () => {
    const [transport] = connected()
    expect(transport.disconnectCount).toBe(0)
  })

  it('disconnectCount increments on first disconnect only', () => {
    const [transport] = connected()
    transport.disconnect()
    transport.disconnect()
    expect(transport.disconnectCount).toBe(1)
  })
})
