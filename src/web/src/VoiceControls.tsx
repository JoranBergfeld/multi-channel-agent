/**
 * Accessible voice controls component. Owns the voice session lifecycle from the browser side:
 * prepare → admit → connect → heartbeat → end. Renders accessible start/end/mute buttons,
 * phase status, partial transcript, warnings, and errors. Does not submit turns (Task 16).
 */
import { useEffect, useReducer, useRef } from 'react'
import { reduce, initialState } from './voiceReducer'
import type { FinalizedUtterance } from './voiceReducer'
import { admitVoice, heartbeatVoice, releaseVoice } from './voiceApi'
import type { VoiceTransport } from './voiceTransport'

export const HEARTBEAT_INTERVAL_MS = 30_000

export interface VoiceControlsProps {
  transport: VoiceTransport
  csrfToken: string
  voiceSessionId: string | null
  onFinalizedUtterance: (utterance: FinalizedUtterance) => void
  onVoiceSessionChanged: (id: string | null) => void
}

export default function VoiceControls({
  transport,
  csrfToken,
  voiceSessionId,
  onFinalizedUtterance,
  onVoiceSessionChanged,
}: VoiceControlsProps) {
  const [state, dispatch] = useReducer(reduce, initialState)

  // Always-current refs — synced in useEffect to avoid reading/writing .current during render.
  // All consumers (event handlers, async callbacks, intervals) fire after effects.
  const stateRef = useRef(state)
  const csrfRef = useRef(csrfToken)
  const onFinalizedRef = useRef(onFinalizedUtterance)
  const onSessionChangedRef = useRef(onVoiceSessionChanged)

  useEffect(() => {
    stateRef.current = state
    csrfRef.current = csrfToken
    onFinalizedRef.current = onFinalizedUtterance
    onSessionChangedRef.current = onVoiceSessionChanged
  })

  const generationRef = useRef(0)
  const heartbeatTimerRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const heartbeatInFlightRef = useRef(false)
  const mountedRef = useRef(true)
  const prevExternalIdRef = useRef(voiceSessionId)
  const abortRef = useRef<AbortController | null>(null)
  const startingRef = useRef(false)

  // ── Helpers ────────────────────────────────────────────────────────────────

  function clearHeartbeat() {
    if (heartbeatTimerRef.current !== null) {
      clearInterval(heartbeatTimerRef.current)
      heartbeatTimerRef.current = null
    }
    heartbeatInFlightRef.current = false
  }

  /** Abort any in-flight admission and clear the synchronous start gate. */
  function abortAdmission() {
    if (abortRef.current) {
      abortRef.current.abort()
      abortRef.current = null
    }
    startingRef.current = false
  }

  /**
   * Invalidate the current generation, cancel heartbeats and in-flight admission,
   * and disconnect a given transport instance. Extracted so the effect cleanup
   * function does not access .current on refs directly (satisfies exhaustive-deps).
   */
  function invalidateSession(t: VoiceTransport) {
    mountedRef.current = false
    generationRef.current++
    clearHeartbeat()
    abortAdmission()
    t.disconnect()
  }

  function startHeartbeat(generation: number) {
    clearHeartbeat()
    heartbeatTimerRef.current = setInterval(() => {
      if (heartbeatInFlightRef.current) return
      heartbeatInFlightRef.current = true

      const sid = stateRef.current.voiceSessionId
      if (!sid) {
        heartbeatInFlightRef.current = false
        return
      }

      void (async () => {
        try {
          const result = await heartbeatVoice(sid, csrfRef.current)

          if (generation !== generationRef.current || !mountedRef.current) return

          switch (result.lifecycleState) {
            case 'active':
              break
            case 'warning_due':
              dispatch({
                type: 'session_warning',
                message: result.remainingSeconds !== null
                  ? `Voice session expires in ${result.remainingSeconds}s`
                  : 'Voice session expiring soon',
              })
              break
            case 'expired':
            case 'idle':
            case 'not_found':
              clearHeartbeat()
              dispatch({
                type: 'session_expired',
                reason: result.lifecycleState === 'not_found'
                  ? 'Session not found'
                  : result.forcedCloseReason ?? `Session ${result.lifecycleState}`,
              })
              transport.disconnect()
              onSessionChangedRef.current(null)
              break
          }
        } catch (err) {
          if (generation !== generationRef.current || !mountedRef.current) return
          clearHeartbeat()
          dispatch({ type: 'error_occurred', error: err instanceof Error ? err.message : String(err) })
          transport.disconnect()
          onSessionChangedRef.current(null)
        } finally {
          heartbeatInFlightRef.current = false
        }
      })()
    }, HEARTBEAT_INTERVAL_MS)
  }

  // ── Handlers ───────────────────────────────────────────────────────────────

  function handleStart() {
    if (stateRef.current.phase !== 'idle') return
    if (startingRef.current) return

    startingRef.current = true
    const generation = ++generationRef.current
    const controller = new AbortController()
    abortRef.current = controller
    dispatch({ type: 'start_requested' })

    void (async () => {
      try {
        let sdpOffer: string
        try {
          sdpOffer = await transport.prepare()
        } catch (err) {
          if (generation !== generationRef.current || !mountedRef.current) return
          dispatch({
            type: 'error_occurred',
            error: `Microphone access failed: ${err instanceof Error ? err.message : String(err)}`,
          })
          onSessionChangedRef.current(null)
          return
        }

        if (generation !== generationRef.current || !mountedRef.current) return

        let result: Awaited<ReturnType<typeof admitVoice>>
        try {
          result = await admitVoice(sdpOffer, csrfRef.current, controller.signal)
        } catch (err) {
          if (generation !== generationRef.current || !mountedRef.current) return
          dispatch({
            type: 'error_occurred',
            error: err instanceof Error ? err.message : String(err),
          })
          transport.disconnect()
          onSessionChangedRef.current(null)
          return
        }

        if (generation !== generationRef.current || !mountedRef.current) {
          // Server may have committed the session before the abort signal was processed.
          // Best-effort release: SQL expiry is authoritative (Task 16) so cleanup failure
          // is intentionally swallowed — it cannot resurrect component state.
          if (result.admitted && result.voiceSessionId) {
            void releaseVoice(result.voiceSessionId, csrfRef.current).catch(() => {})
          }
          transport.disconnect()
          return
        }

        if (!result.admitted) {
          dispatch({ type: 'denied', reason: result.denialReason! })
          transport.disconnect()
          onSessionChangedRef.current(null)
          return
        }

        dispatch({
          type: 'admitted',
          voiceSessionId: result.voiceSessionId!,
          sdpAnswer: result.sdpAnswer!,
        })
        onSessionChangedRef.current(result.voiceSessionId!)

        transport.connect(result.sdpAnswer!, {
          onConnected: () => {
            if (generation !== generationRef.current || !mountedRef.current) return
            dispatch({ type: 'connected' })
            startHeartbeat(generation)
          },
          onSpeechStarted: () => {
            if (generation !== generationRef.current || !mountedRef.current) return
            const wasPlaying = stateRef.current.phase === 'speaking'
            dispatch({ type: 'speech_started' })
            if (wasPlaying) {
              transport.cancelPlayback(0)
            }
          },
          onSpeechStopped: () => {
            // No reducer action; speechActive cleared by final_transcript / speech_interrupted
          },
          onPartialTranscript: (text: string) => {
            if (generation !== generationRef.current || !mountedRef.current) return
            dispatch({ type: 'partial_transcript', text })
          },
          onFinalTranscript: (text: string, nativeMessageId: string) => {
            if (generation !== generationRef.current || !mountedRef.current) return
            dispatch({ type: 'final_transcript', text, nativeMessageId })
            onFinalizedRef.current({ text, nativeMessageId })
          },
          onPlaybackStarted: () => {
            if (generation !== generationRef.current || !mountedRef.current) return
            dispatch({ type: 'playback_started' })
          },
          onPlaybackDone: () => {
            if (generation !== generationRef.current || !mountedRef.current) return
            dispatch({ type: 'playback_finished' })
          },
          onPlaybackFailed: (error: string) => {
            if (generation !== generationRef.current || !mountedRef.current) return
            dispatch({ type: 'playback_failed', error })
          },
          onPlaybackIntegrityError: (requested: string, received: string) => {
            if (generation !== generationRef.current || !mountedRef.current) return
            dispatch({
              type: 'playback_failed',
              error: `Integrity error: expected "${requested}", got "${received}"`,
            })
          },
          onError: (error: string) => {
            if (generation !== generationRef.current || !mountedRef.current) return
            clearHeartbeat()
            dispatch({ type: 'error_occurred', error })
            transport.disconnect()
            onSessionChangedRef.current(null)
          },
          onMicrophoneFailed: (error: string) => {
            if (generation !== generationRef.current || !mountedRef.current) return
            clearHeartbeat()
            dispatch({ type: 'error_occurred', error: `Microphone error: ${error}` })
            transport.disconnect()
            onSessionChangedRef.current(null)
          },
        }, result.voiceSessionId!)
      } finally {
        startingRef.current = false
        if (abortRef.current === controller) {
          abortRef.current = null
        }
      }
    })()
  }

  function handleEnd() {
    const currentPhase = stateRef.current.phase
    if (currentPhase !== 'listening' && currentPhase !== 'speaking') return

    const generation = ++generationRef.current
    const sessionId = stateRef.current.voiceSessionId

    dispatch({ type: 'end_requested' })
    abortAdmission()
    transport.disconnect()
    clearHeartbeat()

    if (!sessionId) {
      dispatch({ type: 'ended' })
      onSessionChangedRef.current(null)
      return
    }

    void (async () => {
      try {
        await releaseVoice(sessionId, csrfRef.current)
        if (generation !== generationRef.current || !mountedRef.current) return
        dispatch({ type: 'ended' })
        onSessionChangedRef.current(null)
      } catch (err) {
        if (generation !== generationRef.current || !mountedRef.current) return
        dispatch({
          type: 'error_occurred',
          error: `Release failed: ${err instanceof Error ? err.message : String(err)}`,
        })
        onSessionChangedRef.current(null)
      }
    })()
  }

  function handleMuteToggle() {
    const nextMuted = !stateRef.current.muted
    dispatch({ type: 'mute_toggled' })
    transport.setMuted(nextMuted)
  }

  // ── Effects ────────────────────────────────────────────────────────────────

  useEffect(() => {
    mountedRef.current = true
    const currentTransport = transport
    return () => { invalidateSession(currentTransport) }
  }, [transport])

  // Detect external clearing of voiceSessionId
  useEffect(() => {
    const prev = prevExternalIdRef.current
    prevExternalIdRef.current = voiceSessionId
    if (prev !== null && voiceSessionId === null && stateRef.current.phase !== 'idle') {
      generationRef.current++
      clearHeartbeat()
      abortAdmission()
      transport.disconnect()
      dispatch({ type: 'error_occurred', error: 'Voice session ended externally' })
    }
  }, [voiceSessionId, transport])

  // ── Render ─────────────────────────────────────────────────────────────────

  const isActive = state.phase === 'listening' || state.phase === 'speaking'
  const showStart = state.phase === 'idle' || state.phase === 'requesting' || state.phase === 'connecting'

  return (
    <div aria-label="Voice controls">
      {showStart && (
        <button
          type="button"
          onClick={handleStart}
          disabled={state.phase !== 'idle'}
        >
          Start Voice
        </button>
      )}

      {state.phase !== 'idle' && (
        <div role="status" aria-label="Voice status">
          {state.phase === 'requesting' && 'Requesting voice session…'}
          {state.phase === 'connecting' && 'Connecting…'}
          {state.phase === 'listening' && 'Listening'}
          {state.phase === 'speaking' && 'Speaking'}
          {state.phase === 'ending' && 'Ending voice session…'}
        </div>
      )}

      {state.speechActive && (
        <div aria-label="Speech active">Speech detected</div>
      )}

      {state.partialTranscript != null && (
        <div aria-label="Partial transcript" aria-live="polite">
          {state.partialTranscript}
        </div>
      )}

      {isActive && (
        <button
          type="button"
          aria-pressed={state.muted}
          onClick={handleMuteToggle}
        >
          {state.muted ? 'Unmute microphone' : 'Mute microphone'}
        </button>
      )}

      {isActive && (
        <button type="button" onClick={handleEnd}>
          End Voice
        </button>
      )}

      {state.warning && (
        <div role="status" aria-label="Voice warning">{state.warning}</div>
      )}

      {state.error && (
        <div role="alert" aria-label="Voice error">{state.error}</div>
      )}

      {state.playbackFailed && (
        <div role="alert" aria-label="Playback failure">Playback failed</div>
      )}
    </div>
  )
}
