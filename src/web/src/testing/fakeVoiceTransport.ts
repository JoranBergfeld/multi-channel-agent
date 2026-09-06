/**
 * Deterministic, reusable fake implementation of VoiceTransport for use in tests.
 * Imports no DOM, WebRTC, or React dependencies.
 */
import type { VoiceTransport, VoiceTransportCallbacks } from '../voiceTransport'

export class FakeVoiceTransport implements VoiceTransport {
  private _callbacks: VoiceTransportCallbacks | null = null
  private _disconnected = true

  // ── Observability ────────────────────────────────────────────────────────────
  /** Every measuredPlayedDurationMs passed to cancelPlayback(), in call order. */
  public readonly cancelPlaybackCalls: number[] = []
  /** Every text passed to speakCanonical(), in call order. */
  public readonly spokenTexts: string[] = []
  /** Every boolean passed to setMuted(), in call order. */
  public readonly muteHistory: boolean[] = []
  /** Number of successful connect() calls. */
  public connectCount = 0
  /** Number of successful disconnect() calls (idempotent: only first counts). */
  public disconnectCount = 0
  /** SDP answer supplied to the most recent connect() call. */
  public lastConnectSdpAnswer: string | null = null
  /** Current mute state; updated by setMuted(). */
  public isMuted = false

  /** Most recently spoken text, or null if speakCanonical() has never been called. */
  get lastSpokenText(): string | null {
    return this.spokenTexts.length > 0 ? this.spokenTexts[this.spokenTexts.length - 1] : null
  }

  /** True when connected and not disconnected. */
  get isConnected(): boolean {
    return !this._disconnected && this._callbacks !== null
  }

  // ── VoiceTransport ───────────────────────────────────────────────────────────

  async prepare(): Promise<string> {
    return 'v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=fake\r\nt=0 0\r\n'
  }

  connect(sdpAnswer: string, callbacks: VoiceTransportCallbacks, _voiceSessionId: string): void {
    this._callbacks = callbacks
    this._disconnected = false
    this.connectCount++
    this.lastConnectSdpAnswer = sdpAnswer
  }

  disconnect(): void {
    if (!this._disconnected) {
      this._disconnected = true
      this._callbacks = null
      this.disconnectCount++
    }
  }

  setMuted(muted: boolean): void {
    this.isMuted = muted
    this.muteHistory.push(muted)
  }

  cancelPlayback(measuredPlayedDurationMs: number): void {
    if (!this.isConnected) {
      throw new Error('cancelPlayback requires connected transport')
    }
    if (!Number.isFinite(measuredPlayedDurationMs) || measuredPlayedDurationMs < 0) {
      throw new RangeError(
        `cancelPlayback: measuredPlayedDurationMs must be finite and non-negative, got ${measuredPlayedDurationMs}`,
      )
    }
    this.cancelPlaybackCalls.push(measuredPlayedDurationMs)
  }

  speakCanonical(text: string): void {
    if (!this.isConnected) {
      throw new Error('speakCanonical requires connected transport')
    }
    this.spokenTexts.push(text)
  }

  // ── Simulate helpers ─────────────────────────────────────────────────────────

  simulateConnected(): void {
    if (!this._disconnected) this._callbacks?.onConnected()
  }

  simulateSpeechStarted(): void {
    if (!this._disconnected) this._callbacks?.onSpeechStarted()
  }

  simulateSpeechStopped(): void {
    if (!this._disconnected) this._callbacks?.onSpeechStopped()
  }

  simulatePartialTranscript(text: string): void {
    if (!this._disconnected) this._callbacks?.onPartialTranscript(text)
  }

  simulateFinalTranscript(text: string, nativeMessageId: string): void {
    if (!this._disconnected) this._callbacks?.onFinalTranscript(text, nativeMessageId)
  }

  simulatePlaybackStarted(): void {
    if (!this._disconnected) this._callbacks?.onPlaybackStarted()
  }

  simulatePlaybackDone(): void {
    if (!this._disconnected) this._callbacks?.onPlaybackDone()
  }

  simulatePlaybackFailed(error: string): void {
    if (!this._disconnected) this._callbacks?.onPlaybackFailed(error)
  }

  simulatePlaybackIntegrityError(requested: string, received: string): void {
    if (!this._disconnected) this._callbacks?.onPlaybackIntegrityError(requested, received)
  }

  simulateError(error: string): void {
    if (!this._disconnected) this._callbacks?.onError(error)
  }

  simulateMicrophoneFailed(error: string): void {
    if (!this._disconnected) this._callbacks?.onMicrophoneFailed(error)
  }
}
