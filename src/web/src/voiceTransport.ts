/**
 * Typed transport contract for the browser-side voice layer.
 * No React, DOM, or WebRTC imports — exercisable in pure TypeScript / Node test environments.
 */

/**
 * Callbacks invoked by a VoiceTransport. All are synchronous and delivered on the transport's
 * internal event loop. No uncertain, confidence, logprobs, provider credential, or
 * control-session fields are exposed.
 */
export interface VoiceTransportCallbacks {
  onConnected: () => void
  onSpeechStarted: () => void
  onSpeechStopped: () => void
  onPartialTranscript: (text: string) => void
  /**
   * Fired when the provider delivers a final transcription.
   *
   * nativeMessageId is derived as `voice:${voiceSessionId}:${itemId}`, where
   * - itemId is the provider's item_id from the conversation.item.input_audio_transcription.completed event
   * - voiceSessionId is the server-attested session ID supplied by the orchestrator after admission
   *
   * No uncertain, confidence, logprobs, or provider-credential fields are forwarded.
   */
  onFinalTranscript: (text: string, nativeMessageId: string) => void
  onPlaybackStarted: () => void
  onPlaybackDone: () => void
  onPlaybackFailed: (error: string) => void
  /**
   * Fired when response.audio_transcript.done.transcript differs from the text passed to
   * speakCanonical(). The real transport stops playback on mismatch.
   */
  onPlaybackIntegrityError: (requested: string, received: string) => void
  onError: (error: string) => void
  onMicrophoneFailed: (error: string) => void
}

/**
 * Transport boundary between the browser UI and the voice provider.
 * No browser RTCPeerConnection, getUserMedia, or provider credentials are exposed;
 * all provider-side communication is brokered by the server.
 */
export interface VoiceTransport {
  /**
   * Prepares the local transport and returns an SDP offer string.
   * Must be called once before connect(). The real implementation acquires microphone
   * access and constructs a WebRTC offer. The SDP string is forwarded to the server
   * for admission negotiation.
   */
  prepare(): Promise<string>

  /**
   * Establishes the voice session using the SDP answer from the server orchestrator.
   * All callbacks are delivered after connect() returns until disconnect() is called.
   * Calling connect() again starts a fresh session; previous callbacks are discarded.
   */
  connect(sdpAnswer: string, callbacks: VoiceTransportCallbacks): void

  /**
   * Tears down the session. Subsequent callbacks are suppressed and callback references
   * are released. Safe to call more than once; subsequent calls are no-ops.
   */
  disconnect(): void

  /**
   * Mutes or unmutes the local microphone. The real transport applies the change to the
   * underlying MediaStreamTrack. May be called before connect() or after disconnect();
   * those calls are recorded but have no audio effect.
   */
  setMuted(muted: boolean): void

  /**
   * Interrupts provider playback.
   *
   * The real transport sends response.cancel then conversation.item.truncate to the
   * provider control WebSocket. The truncate message carries:
   *   - item_id: the last tracked output item_id from provider response events
   *   - content_index: 0 (invariant)
   *   - audio_end_ms: measuredPlayedDurationMs (must be finite and non-negative)
   *
   * Throws RangeError if measuredPlayedDurationMs is not finite or is negative.
   */
  cancelPlayback(measuredPlayedDurationMs: number): void

  /**
   * Sends canonical text for voice playback.
   *
   * The real transport sends response.create with response.pre_generated_assistant_message
   * set to the exact canonical text. It then listens for response.audio_transcript.done and
   * verifies that transcript equals the requested text. A mismatch fires
   * onPlaybackIntegrityError and stops playback.
   *
   * The UI always keeps the canonical summary text visible regardless of playback state.
   */
  speakCanonical(text: string): void
}
