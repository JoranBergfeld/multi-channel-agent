/**
 * Pure reducer for the browser-side voice session state machine. Has no React, DOM, or WebRTC
 * dependencies — it can be exercised in Node and unit-tested without a browser environment.
 *
 * Phases:
 *   idle → requesting → connecting → listening ⇄ speaking → ending → idle
 *
 * Every transition returns a new object; the prior state is never mutated.
 */

// ── Types ─────────────────────────────────────────────────────────────────────

export type VoicePhase = 'idle' | 'requesting' | 'connecting' | 'listening' | 'speaking' | 'ending'

/** The completed utterance the caller should submit to the conversation API. Contains exactly the
 * two fields needed for submission — no confidence scores, logprobs, phrases, or provider IDs. */
export interface FinalizedUtterance {
  readonly text: string
  readonly nativeMessageId: string
}

export interface VoiceState {
  readonly phase: VoicePhase
  readonly voiceSessionId: string | null
  readonly muted: boolean
  readonly speechActive: boolean
  readonly bargeIn: boolean
  readonly partialTranscript: string | null
  readonly finalizedUtterance: FinalizedUtterance | null
  readonly warning: string | null
  readonly warningDelivered: boolean
  readonly error: string | null
  readonly playbackFailed: boolean
}

export type VoiceAction =
  | { type: 'start_requested' }
  | { type: 'admitted'; voiceSessionId: string; sdpAnswer: string }
  | { type: 'denied'; reason: string }
  | { type: 'connected' }
  | { type: 'speech_started' }
  | { type: 'partial_transcript'; text: string }
  | { type: 'final_transcript'; text: string; nativeMessageId: string }
  | { type: 'speech_interrupted' }
  | { type: 'playback_started' }
  | { type: 'playback_finished' }
  | { type: 'playback_failed'; error: string }
  | { type: 'session_warning'; message: string }
  | { type: 'session_expired'; reason: string }
  | { type: 'end_requested' }
  | { type: 'ended' }
  | { type: 'error_occurred'; error: string }
  | { type: 'mute_toggled' }
  | { type: 'utterance_submitted' }

// ── Initial state ─────────────────────────────────────────────────────────────

export const initialState: VoiceState = {
  phase: 'idle',
  voiceSessionId: null,
  muted: false,
  speechActive: false,
  bargeIn: false,
  partialTranscript: null,
  finalizedUtterance: null,
  warning: null,
  warningDelivered: false,
  error: null,
  playbackFailed: false,
}

// ── Helpers ───────────────────────────────────────────────────────────────────

/** All ephemeral per-session fields reset to safe defaults. */
function clearEphemeral(s: VoiceState): VoiceState {
  return {
    ...s,
    voiceSessionId: null,
    speechActive: false,
    bargeIn: false,
    partialTranscript: null,
    finalizedUtterance: null,
  }
}

const DENIAL_MESSAGES: Record<string, string> = {
  VoiceDisabled: 'Voice is currently disabled.',
  AlreadyActive: 'A voice session is already active.',
  GlobalCapReached: 'Voice capacity has been reached; please try again later.',
}

function denialMessage(reason: string): string {
  return DENIAL_MESSAGES[reason] ?? `Session denied: ${reason}`
}

// ── Reducer ───────────────────────────────────────────────────────────────────

export function reduce(state: VoiceState, action: VoiceAction): VoiceState {
  switch (action.type) {
    case 'start_requested':
      return {
        ...clearEphemeral(state),
        phase: 'requesting',
        warning: null,
        warningDelivered: false,
        error: null,
        playbackFailed: false,
        // muted is intentionally retained: the user may prefer to start muted
      }

    case 'admitted':
      if (state.phase !== 'requesting') return state
      return { ...state, phase: 'connecting', voiceSessionId: action.voiceSessionId }

    case 'denied':
      if (state.phase !== 'requesting') return state
      return {
        ...clearEphemeral({ ...state, phase: 'idle' }),
        phase: 'idle',
        error: denialMessage(action.reason),
      }

    case 'connected':
      if (state.phase !== 'connecting') return state
      return { ...state, phase: 'listening' }

    case 'speech_started': {
      if (state.phase === 'speaking') {
        // User is speaking while the assistant is playing back → barge-in
        return { ...state, phase: 'listening', speechActive: true, bargeIn: true }
      }
      if (state.phase === 'listening') {
        return { ...state, speechActive: true }
      }
      return state
    }

    case 'partial_transcript':
      if (state.phase !== 'listening' && state.phase !== 'speaking') return state
      return { ...state, partialTranscript: action.text }

    case 'final_transcript':
      if (state.phase !== 'listening' && state.phase !== 'speaking') return state
      return {
        ...state,
        finalizedUtterance: { text: action.text, nativeMessageId: action.nativeMessageId },
        partialTranscript: null,
        speechActive: false,
        bargeIn: false,
      }

    case 'speech_interrupted':
      if (state.phase !== 'listening' && state.phase !== 'speaking') return state
      return {
        ...state,
        partialTranscript: null,
        finalizedUtterance: null,
        speechActive: false,
        bargeIn: false,
      }

    case 'playback_started':
      if (state.phase !== 'listening') return state
      return { ...state, phase: 'speaking' }

    case 'playback_finished':
      if (state.phase !== 'speaking') return state
      return { ...state, phase: 'listening' }

    case 'playback_failed':
      if (state.phase !== 'speaking') return state
      return { ...state, phase: 'listening', playbackFailed: true, error: action.error }

    case 'session_warning': {
      const warningPhases: VoicePhase[] = ['connecting', 'listening', 'speaking']
      if (!warningPhases.includes(state.phase)) return state
      if (state.warningDelivered) return state
      return { ...state, warning: action.message, warningDelivered: true }
    }

    case 'session_expired':
      return {
        ...clearEphemeral({ ...state, phase: 'idle' }),
        phase: 'idle',
        error: `Session expired: ${action.reason}`,
      }

    case 'end_requested':
      if (state.phase !== 'listening' && state.phase !== 'speaking') return state
      return { ...state, phase: 'ending' }

    case 'ended':
      if (state.phase !== 'ending') return state
      return {
        ...initialState,
        // retain the mute preference the user set during the session
        muted: state.muted,
      }

    case 'error_occurred':
      return {
        ...clearEphemeral({ ...state, phase: 'idle' }),
        phase: 'idle',
        error: action.error,
      }

    case 'mute_toggled':
      return { ...state, muted: !state.muted }

    case 'utterance_submitted':
      return { ...state, finalizedUtterance: null, bargeIn: false }

    default: {
      const exhaustive: never = action
      throw new Error(`Unhandled voice action: ${(exhaustive as VoiceAction).type}`)
    }
  }
}
