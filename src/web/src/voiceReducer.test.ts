import { describe, expect, it } from 'vitest'

import {
  initialState,
  reduce,
  type FinalizedUtterance,
  type VoiceAction,
  type VoiceState,
} from './voiceReducer'

// ── helpers ──────────────────────────────────────────────────────────────────

function apply(state: VoiceState, ...actions: VoiceAction[]): VoiceState {
  return actions.reduce((s, a) => reduce(s, a), state)
}

function activeState(): VoiceState {
  return apply(
    initialState,
    { type: 'start_requested' },
    { type: 'admitted', voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n...' },
    { type: 'connected' },
  )
}

// ── 1. initial state ──────────────────────────────────────────────────────────

describe('initialState', () => {
  it('starts idle with a null session and safe defaults', () => {
    expect(initialState.phase).toBe('idle')
    expect(initialState.voiceSessionId).toBeNull()
    expect(initialState.muted).toBe(false)
    expect(initialState.speechActive).toBe(false)
    expect(initialState.bargeIn).toBe(false)
    expect(initialState.partialTranscript).toBeNull()
    expect(initialState.finalizedUtterance).toBeNull()
    expect(initialState.warning).toBeNull()
    expect(initialState.warningDelivered).toBe(false)
    expect(initialState.error).toBeNull()
    expect(initialState.playbackFailed).toBe(false)
  })
})

// ── 2. start_requested ────────────────────────────────────────────────────────

describe('start_requested', () => {
  it('transitions to requesting', () => {
    const s = reduce(initialState, { type: 'start_requested' })
    expect(s.phase).toBe('requesting')
  })

  it('clears stale session, transcript, warning, error, playbackFailed and barge-in', () => {
    const stale: VoiceState = {
      ...initialState,
      voiceSessionId: 'old-session',
      partialTranscript: 'partial',
      finalizedUtterance: { text: 'hello', nativeMessageId: 'msg-1' },
      warning: 'some warning',
      warningDelivered: true,
      error: 'previous error',
      playbackFailed: true,
      bargeIn: true,
      speechActive: true,
    }
    const s = reduce(stale, { type: 'start_requested' })
    expect(s.voiceSessionId).toBeNull()
    expect(s.partialTranscript).toBeNull()
    expect(s.finalizedUtterance).toBeNull()
    expect(s.warning).toBeNull()
    expect(s.warningDelivered).toBe(false)
    expect(s.error).toBeNull()
    expect(s.playbackFailed).toBe(false)
    expect(s.bargeIn).toBe(false)
    expect(s.speechActive).toBe(false)
  })

  it('retains the current muted setting', () => {
    const mutedState: VoiceState = { ...initialState, muted: true }
    const s = reduce(mutedState, { type: 'start_requested' })
    expect(s.muted).toBe(true)
  })
})

// ── 3. admitted ───────────────────────────────────────────────────────────────

describe('admitted', () => {
  it('transitions from requesting to connecting with the voiceSessionId', () => {
    const requesting = reduce(initialState, { type: 'start_requested' })
    const s = reduce(requesting, { type: 'admitted', voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n...' })
    expect(s.phase).toBe('connecting')
    expect(s.voiceSessionId).toBe('vs-1')
  })

  it('does not store the SDP answer in state', () => {
    const requesting = reduce(initialState, { type: 'start_requested' })
    const s = reduce(requesting, { type: 'admitted', voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n...' })
    expect(s).not.toHaveProperty('sdpAnswer')
  })

  it('is ignored (no-op) when not in requesting phase', () => {
    const s = reduce(initialState, { type: 'admitted', voiceSessionId: 'vs-2', sdpAnswer: 'v=0\r\n...' })
    expect(s.phase).toBe('idle')
    expect(s.voiceSessionId).toBeNull()
  })
})

// ── 4. denied ─────────────────────────────────────────────────────────────────

describe('denied', () => {
  it('returns to idle with a human-readable error', () => {
    const requesting = reduce(initialState, { type: 'start_requested' })
    const s = reduce(requesting, { type: 'denied', reason: 'VoiceDisabled' })
    expect(s.phase).toBe('idle')
    expect(s.error).toBeTruthy()
    expect(typeof s.error).toBe('string')
  })

  it('clears session and ephemeral state on denial', () => {
    const requesting: VoiceState = {
      ...initialState,
      phase: 'requesting',
      voiceSessionId: null,
      partialTranscript: 'stale',
      speechActive: true,
    }
    const s = reduce(requesting, { type: 'denied', reason: 'AlreadyActive' })
    expect(s.voiceSessionId).toBeNull()
    expect(s.partialTranscript).toBeNull()
    expect(s.speechActive).toBe(false)
  })

  it('maps VoiceDisabled to a useful message', () => {
    const s = reduce({ ...initialState, phase: 'requesting' }, { type: 'denied', reason: 'VoiceDisabled' })
    expect(s.error).toMatch(/disabled/i)
  })

  it('maps AlreadyActive to a useful message', () => {
    const s = reduce({ ...initialState, phase: 'requesting' }, { type: 'denied', reason: 'AlreadyActive' })
    expect(s.error).toMatch(/already active/i)
  })

  it('maps GlobalCapReached to a useful message', () => {
    const s = reduce({ ...initialState, phase: 'requesting' }, { type: 'denied', reason: 'GlobalCapReached' })
    expect(s.error).toMatch(/capacity|cap|limit/i)
  })

  it('handles unknown denial reason without throwing', () => {
    expect(() =>
      reduce({ ...initialState, phase: 'requesting' }, { type: 'denied', reason: 'SomeUnknownReason' }),
    ).not.toThrow()
    const s = reduce({ ...initialState, phase: 'requesting' }, { type: 'denied', reason: 'SomeUnknownReason' })
    expect(s.error).toBeTruthy()
  })
})

// ── 5. connected ──────────────────────────────────────────────────────────────

describe('connected', () => {
  it('transitions from connecting to listening', () => {
    const connecting = apply(initialState, { type: 'start_requested' }, {
      type: 'admitted',
      voiceSessionId: 'vs-1',
      sdpAnswer: 'v=0',
    })
    const s = reduce(connecting, { type: 'connected' })
    expect(s.phase).toBe('listening')
  })

  it('is ignored when not in connecting phase', () => {
    const s = reduce(initialState, { type: 'connected' })
    expect(s.phase).toBe('idle')
  })
})

// ── 6. speech_started ─────────────────────────────────────────────────────────

describe('speech_started', () => {
  it('sets speechActive while listening', () => {
    const s = reduce(activeState(), { type: 'speech_started' })
    expect(s.speechActive).toBe(true)
    expect(s.phase).toBe('listening')
  })

  it('when speaking (TTS), sets bargeIn and transitions back to listening', () => {
    const speaking = apply(activeState(), { type: 'playback_started' })
    expect(speaking.phase).toBe('speaking')

    const s = reduce(speaking, { type: 'speech_started' })
    expect(s.phase).toBe('listening')
    expect(s.speechActive).toBe(true)
    expect(s.bargeIn).toBe(true)
  })

  it('is ignored when idle', () => {
    const s = reduce(initialState, { type: 'speech_started' })
    expect(s.speechActive).toBe(false)
    expect(s.phase).toBe('idle')
  })
})

// ── 7. partial_transcript ─────────────────────────────────────────────────────

describe('partial_transcript', () => {
  it('sets partialTranscript without creating a finalizedUtterance', () => {
    const s = reduce(activeState(), { type: 'partial_transcript', text: 'add som' })
    expect(s.partialTranscript).toBe('add som')
    expect(s.finalizedUtterance).toBeNull()
  })

  it('is ignored when idle', () => {
    const s = reduce(initialState, { type: 'partial_transcript', text: 'stale' })
    expect(s.partialTranscript).toBeNull()
  })
})

// ── 8. final_transcript ───────────────────────────────────────────────────────

describe('final_transcript', () => {
  it('creates a FinalizedUtterance with exactly text and nativeMessageId', () => {
    const withPartial = reduce(activeState(), { type: 'partial_transcript', text: 'add two' })
    const s = reduce(withPartial, { type: 'final_transcript', text: 'add two kg', nativeMessageId: 'msg-42' })
    const utterance = s.finalizedUtterance as FinalizedUtterance
    expect(utterance.text).toBe('add two kg')
    expect(utterance.nativeMessageId).toBe('msg-42')
    expect(Object.keys(utterance)).toStrictEqual(['text', 'nativeMessageId'])
  })

  it('clears partialTranscript and speechActive', () => {
    const pre = { ...activeState(), partialTranscript: 'add two', speechActive: true }
    const s = reduce(pre, { type: 'final_transcript', text: 'add two kg', nativeMessageId: 'msg-42' })
    expect(s.partialTranscript).toBeNull()
    expect(s.speechActive).toBe(false)
  })

  it('resets bargeIn', () => {
    const pre = { ...activeState(), bargeIn: true }
    const s = reduce(pre, { type: 'final_transcript', text: 'stop', nativeMessageId: 'msg-43' })
    expect(s.bargeIn).toBe(false)
  })

  it('is ignored when idle', () => {
    const s = reduce(initialState, { type: 'final_transcript', text: 'stale', nativeMessageId: 'msg-99' })
    expect(s.finalizedUtterance).toBeNull()
  })
})

// ── 9. speech_interrupted ─────────────────────────────────────────────────────

describe('speech_interrupted', () => {
  it('clears partial, finalizedUtterance, speechActive, and bargeIn', () => {
    const pre: VoiceState = {
      ...activeState(),
      partialTranscript: 'some text',
      finalizedUtterance: { text: 'hello', nativeMessageId: 'msg-1' },
      speechActive: true,
      bargeIn: true,
    }
    const s = reduce(pre, { type: 'speech_interrupted' })
    expect(s.partialTranscript).toBeNull()
    expect(s.finalizedUtterance).toBeNull()
    expect(s.speechActive).toBe(false)
    expect(s.bargeIn).toBe(false)
  })

  it('is ignored when idle', () => {
    const s = reduce(initialState, { type: 'speech_interrupted' })
    expect(s.phase).toBe('idle')
  })
})

// ── 10. playback lifecycle ────────────────────────────────────────────────────

describe('playback_started', () => {
  it('transitions from listening to speaking', () => {
    const s = reduce(activeState(), { type: 'playback_started' })
    expect(s.phase).toBe('speaking')
  })

  it('is ignored when idle', () => {
    const s = reduce(initialState, { type: 'playback_started' })
    expect(s.phase).toBe('idle')
  })
})

describe('playback_finished', () => {
  it('transitions from speaking back to listening', () => {
    const speaking = reduce(activeState(), { type: 'playback_started' })
    const s = reduce(speaking, { type: 'playback_finished' })
    expect(s.phase).toBe('listening')
  })

  it('is ignored when not speaking', () => {
    const s = reduce(activeState(), { type: 'playback_finished' })
    expect(s.phase).toBe('listening')
  })
})

describe('playback_failed', () => {
  it('transitions from speaking to listening with playbackFailed and error', () => {
    const speaking = reduce(activeState(), { type: 'playback_started' })
    const s = reduce(speaking, { type: 'playback_failed', error: 'decode error' })
    expect(s.phase).toBe('listening')
    expect(s.playbackFailed).toBe(true)
    expect(s.error).toBe('decode error')
  })

  it('is ignored when not speaking', () => {
    const s = reduce(activeState(), { type: 'playback_failed', error: 'decode error' })
    expect(s.playbackFailed).toBe(false)
  })
})

// ── 11. session_warning ───────────────────────────────────────────────────────

describe('session_warning', () => {
  it('sets warning and marks it undelivered on first warning', () => {
    const s = reduce(activeState(), { type: 'session_warning', message: 'nearing limit' })
    expect(s.warning).toBe('nearing limit')
    expect(s.warningDelivered).toBe(false)
  })

  it('ignores subsequent warnings once warningDelivered is true', () => {
    const pre: VoiceState = { ...activeState(), warning: 'first', warningDelivered: true }
    const s = reduce(pre, { type: 'session_warning', message: 'second' })
    expect(s.warning).toBe('first')
  })
})

// ── 12. session_expired ───────────────────────────────────────────────────────

describe('session_expired', () => {
  it('returns to idle with reason visible as error', () => {
    const s = reduce(activeState(), { type: 'session_expired', reason: 'timeout' })
    expect(s.phase).toBe('idle')
    expect(s.error).toBeTruthy()
    expect(s.error).toContain('timeout')
  })

  it('clears session and ephemeral state', () => {
    const pre: VoiceState = {
      ...activeState(),
      partialTranscript: 'some',
      speechActive: true,
      bargeIn: true,
      finalizedUtterance: { text: 'x', nativeMessageId: 'y' },
    }
    const s = reduce(pre, { type: 'session_expired', reason: 'max-duration' })
    expect(s.voiceSessionId).toBeNull()
    expect(s.partialTranscript).toBeNull()
    expect(s.speechActive).toBe(false)
    expect(s.bargeIn).toBe(false)
    expect(s.finalizedUtterance).toBeNull()
  })
})

// ── 13. end_requested / ended ─────────────────────────────────────────────────

describe('end_requested / ended', () => {
  it('transitions from listening to ending', () => {
    const s = reduce(activeState(), { type: 'end_requested' })
    expect(s.phase).toBe('ending')
  })

  it('transitions from speaking to ending', () => {
    const speaking = reduce(activeState(), { type: 'playback_started' })
    const s = reduce(speaking, { type: 'end_requested' })
    expect(s.phase).toBe('ending')
  })

  it('ended produces a clean idle state', () => {
    const ending = apply(activeState(), { type: 'end_requested' })
    const s = reduce(ending, { type: 'ended' })
    expect(s.phase).toBe('idle')
    expect(s.voiceSessionId).toBeNull()
    expect(s.speechActive).toBe(false)
    expect(s.bargeIn).toBe(false)
    expect(s.partialTranscript).toBeNull()
    expect(s.finalizedUtterance).toBeNull()
    expect(s.error).toBeNull()
    expect(s.playbackFailed).toBe(false)
  })
})

// ── 14. error_occurred ────────────────────────────────────────────────────────

describe('error_occurred', () => {
  it('returns to idle with error and clears session/ephemeral state', () => {
    const pre: VoiceState = {
      ...activeState(),
      partialTranscript: 'some',
      speechActive: true,
      bargeIn: true,
    }
    const s = reduce(pre, { type: 'error_occurred', error: 'network failure' })
    expect(s.phase).toBe('idle')
    expect(s.error).toBe('network failure')
    expect(s.voiceSessionId).toBeNull()
    expect(s.partialTranscript).toBeNull()
    expect(s.speechActive).toBe(false)
    expect(s.bargeIn).toBe(false)
  })
})

// ── 15. mute_toggled ──────────────────────────────────────────────────────────

describe('mute_toggled', () => {
  it('flips muted from false to true', () => {
    const s = reduce(initialState, { type: 'mute_toggled' })
    expect(s.muted).toBe(true)
  })

  it('flips muted from true to false', () => {
    const s = reduce({ ...initialState, muted: true }, { type: 'mute_toggled' })
    expect(s.muted).toBe(false)
  })

  it('works in any phase', () => {
    const s = reduce(activeState(), { type: 'mute_toggled' })
    expect(s.muted).toBe(true)
  })
})

// ── 16. utterance_submitted ───────────────────────────────────────────────────

describe('utterance_submitted', () => {
  it('clears finalizedUtterance and bargeIn', () => {
    const pre: VoiceState = {
      ...activeState(),
      finalizedUtterance: { text: 'confirm', nativeMessageId: 'msg-5' },
      bargeIn: true,
    }
    const s = reduce(pre, { type: 'utterance_submitted' })
    expect(s.finalizedUtterance).toBeNull()
    expect(s.bargeIn).toBe(false)
  })
})

// ── 17. no mutation / stale events ───────────────────────────────────────────

describe('immutability', () => {
  it('does not mutate the source state', () => {
    const before = { ...initialState }
    const frozen = Object.freeze({ ...initialState })
    expect(() => reduce(frozen, { type: 'start_requested' })).not.toThrow()
    expect(initialState).toStrictEqual(before)
  })
})

describe('stale async events after terminal state', () => {
  it('admitted after ended does not reactivate a session', () => {
    const ended = apply(activeState(), { type: 'end_requested' }, { type: 'ended' })
    const s = reduce(ended, { type: 'admitted', voiceSessionId: 'stale', sdpAnswer: 'v=0' })
    expect(s.phase).toBe('idle')
    expect(s.voiceSessionId).toBeNull()
  })

  it('connected after ended does not change phase', () => {
    const ended = apply(activeState(), { type: 'end_requested' }, { type: 'ended' })
    const s = reduce(ended, { type: 'connected' })
    expect(s.phase).toBe('idle')
  })

  it('final_transcript after ended does not populate state', () => {
    const ended = apply(activeState(), { type: 'end_requested' }, { type: 'ended' })
    const s = reduce(ended, { type: 'final_transcript', text: 'stale', nativeMessageId: 'msg-x' })
    expect(s.finalizedUtterance).toBeNull()
  })

  it('playback_started after error_occurred does not reactivate', () => {
    const errored = reduce(activeState(), { type: 'error_occurred', error: 'boom' })
    const s = reduce(errored, { type: 'playback_started' })
    expect(s.phase).toBe('idle')
  })

  it('final_transcript after error_occurred does not populate state', () => {
    const errored = reduce(activeState(), { type: 'error_occurred', error: 'boom' })
    const s = reduce(errored, { type: 'final_transcript', text: 'stale', nativeMessageId: 'msg-z' })
    expect(s.finalizedUtterance).toBeNull()
  })
})
