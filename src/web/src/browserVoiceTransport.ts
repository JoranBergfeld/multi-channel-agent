/**
 * Production browser-side voice transport using RTCPeerConnection, getUserMedia, and a data
 * channel. Brokers audio/events between the local microphone/speaker and the server-proxied
 * voice provider. Never embeds Azure endpoints, tokens, or credentials — all provider
 * communication is brokered by the backend through the SDP exchange.
 */
import type { VoiceTransport, VoiceTransportCallbacks } from './voiceTransport'

/**
 * ICE gathering timeout in milliseconds. Prevents indefinite hangs if the network
 * adapter cannot enumerate candidates.
 */
const ICE_GATHERING_TIMEOUT_MS = 10_000

export class BrowserVoiceTransport implements VoiceTransport {
  private _peer: RTCPeerConnection | null = null
  private _dataChannel: RTCDataChannel | null = null
  private _stream: MediaStream | null = null
  private _callbacks: VoiceTransportCallbacks | null = null
  private _disconnected = true
  private _voiceSessionId: string | null = null
  private _lastOutputItemId: string | null = null
  private _pendingCanonicalText: string | null = null
  private _playbackStartedFired = false
  private _audioElement: HTMLAudioElement | null = null

  // ── VoiceTransport ───────────────────────────────────────────────────────

  async prepare(): Promise<string> {
    let stream: MediaStream | null = null
    let peer: RTCPeerConnection | null = null

    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true })

      peer = new RTCPeerConnection()
      this._peer = peer
      this._stream = stream

      // Create the data channel for provider events
      this._dataChannel = peer.createDataChannel('oai-events')

      // Add audio tracks to peer connection
      for (const track of stream.getAudioTracks()) {
        peer.addTrack(track, stream)
      }

      const offer = await peer.createOffer()
      await peer.setLocalDescription(offer)

      // Wait for ICE gathering to complete (bounded)
      if (peer.iceGatheringState !== 'complete') {
        await this._waitForIceGathering(peer)
      }

      const sdp = peer.localDescription?.sdp
      if (!sdp) {
        throw new Error('Failed to generate local SDP offer')
      }

      return sdp
    } catch (err) {
      // Cleanup on failure
      if (stream) {
        for (const track of stream.getTracks()) {
          track.stop()
        }
      }
      if (peer) {
        peer.close()
      }
      this._peer = null
      this._dataChannel = null
      this._stream = null
      throw err
    }
  }

  connect(sdpAnswer: string, callbacks: VoiceTransportCallbacks, voiceSessionId: string): void {
    if (!this._peer || !this._dataChannel) {
      throw new Error('connect requires a prepared transport — call prepare() first')
    }

    // Replace callbacks (connect-over-connect discards old ones)
    this._callbacks = callbacks
    this._disconnected = false
    this._voiceSessionId = voiceSessionId
    this._lastOutputItemId = null
    this._pendingCanonicalText = null
    this._playbackStartedFired = false

    const peer = this._peer
    const dc = this._dataChannel

    // Set remote SDP answer
    const desc = new RTCSessionDescription({ type: 'answer', sdp: sdpAnswer })
    void peer.setRemoteDescription(desc)

    // Wire data channel events
    dc.onopen = () => {
      if (this._disconnected || this._callbacks !== callbacks) return
      callbacks.onConnected()
    }

    dc.onmessage = (event: MessageEvent) => {
      if (this._disconnected || this._callbacks !== callbacks) return
      this._handleDataChannelMessage(event.data as string, callbacks)
    }

    dc.onerror = () => {
      if (this._disconnected || this._callbacks !== callbacks) return
      callbacks.onError('Data channel error')
    }

    // Wire peer connection state changes
    peer.onconnectionstatechange = () => {
      if (this._disconnected || this._callbacks !== callbacks) return
      if (peer.connectionState === 'failed') {
        callbacks.onError('Peer connection failed')
      }
    }

    // Wire remote audio playback
    peer.ontrack = (event: RTCTrackEvent) => {
      if (this._disconnected || this._callbacks !== callbacks) return
      if (event.streams?.[0]) {
        this._playRemoteAudio(event.streams[0])
      }
    }
  }

  disconnect(): void {
    if (this._disconnected) return

    this._disconnected = true
    this._callbacks = null
    this._voiceSessionId = null
    this._lastOutputItemId = null
    this._pendingCanonicalText = null
    this._playbackStartedFired = false

    if (this._dataChannel) {
      this._dataChannel.onopen = null
      this._dataChannel.onmessage = null
      this._dataChannel.onerror = null
      this._dataChannel.close()
      this._dataChannel = null
    }

    if (this._peer) {
      this._peer.onconnectionstatechange = null
      this._peer.ontrack = null
      this._peer.onicecandidate = null
      this._peer.onicegatheringstatechange = null
      this._peer.close()
      this._peer = null
    }

    if (this._stream) {
      for (const track of this._stream.getTracks()) {
        track.stop()
      }
      this._stream = null
    }

    if (this._audioElement) {
      this._audioElement.srcObject = null
      this._audioElement = null
    }
  }

  setMuted(muted: boolean): void {
    if (this._stream) {
      for (const track of this._stream.getAudioTracks()) {
        track.enabled = !muted
      }
    }
  }

  cancelPlayback(measuredPlayedDurationMs: number): void {
    if (this._disconnected || !this._dataChannel) {
      throw new Error('cancelPlayback requires connected transport')
    }
    if (!Number.isFinite(measuredPlayedDurationMs) || measuredPlayedDurationMs < 0) {
      throw new RangeError(
        `cancelPlayback: measuredPlayedDurationMs must be finite and non-negative, got ${measuredPlayedDurationMs}`,
      )
    }

    this._sendDataChannel({ type: 'response.cancel' })

    if (this._lastOutputItemId) {
      this._sendDataChannel({
        type: 'conversation.item.truncate',
        item_id: this._lastOutputItemId,
        content_index: 0,
        audio_end_ms: measuredPlayedDurationMs,
      })
    }
  }

  speakCanonical(text: string): void {
    if (this._disconnected || !this._dataChannel) {
      throw new Error('speakCanonical requires connected transport')
    }

    this._pendingCanonicalText = text
    this._sendDataChannel({
      type: 'response.create',
      response: {
        pre_generated_assistant_message: text,
      },
    })
  }

  // ── Internals ──────────────────────────────────────────────────────────────

  private _waitForIceGathering(peer: RTCPeerConnection): Promise<void> {
    return new Promise<void>((resolve, _reject) => {
      const timer = setTimeout(() => {
        // Resolve with whatever we have — partial candidates are usable
        resolve()
      }, ICE_GATHERING_TIMEOUT_MS)

      peer.onicegatheringstatechange = () => {
        if (peer.iceGatheringState === 'complete') {
          clearTimeout(timer)
          peer.onicegatheringstatechange = null
          resolve()
        }
      }

      // Also resolve on null candidate (signals gathering complete)
      peer.onicecandidate = (event) => {
        if (event.candidate === null) {
          clearTimeout(timer)
          peer.onicegatheringstatechange = null
          peer.onicecandidate = null
          resolve()
        }
      }

      // If already complete
      if (peer.iceGatheringState === 'complete') {
        clearTimeout(timer)
        resolve()
      }
    })
  }

  private _sendDataChannel(message: Record<string, unknown>): void {
    if (this._dataChannel && this._dataChannel.readyState === 'open') {
      this._dataChannel.send(JSON.stringify(message))
    }
  }

  private _handleDataChannelMessage(data: string, callbacks: VoiceTransportCallbacks): void {
    let parsed: Record<string, unknown>
    try {
      parsed = JSON.parse(data) as Record<string, unknown>
    } catch {
      // Malformed JSON — ignore silently
      return
    }

    const type = parsed['type']
    if (typeof type !== 'string') return

    switch (type) {
      case 'input_audio_buffer.speech_started':
        callbacks.onSpeechStarted()
        break

      case 'input_audio_buffer.speech_stopped':
        callbacks.onSpeechStopped()
        break

      case 'conversation.item.input_audio_transcription.delta':
        if (typeof parsed['delta'] === 'string') {
          callbacks.onPartialTranscript(parsed['delta'])
        }
        break

      case 'conversation.item.input_audio_transcription.completed': {
        const transcript = parsed['transcript']
        const itemId = parsed['item_id']
        if (typeof transcript === 'string' && typeof itemId === 'string' && this._voiceSessionId) {
          const nativeMessageId = `voice:${this._voiceSessionId}:${itemId}`
          callbacks.onFinalTranscript(transcript, nativeMessageId)
        }
        break
      }

      case 'response.audio.delta':
        // Track the output item_id for truncation
        if (typeof parsed['item_id'] === 'string') {
          this._lastOutputItemId = parsed['item_id']
        }
        if (!this._playbackStartedFired) {
          this._playbackStartedFired = true
          callbacks.onPlaybackStarted()
        }
        break

      case 'response.audio.done':
        this._playbackStartedFired = false
        callbacks.onPlaybackDone()
        break

      case 'response.audio_transcript.done': {
        const transcript = parsed['transcript']
        if (typeof transcript === 'string' && this._pendingCanonicalText !== null) {
          if (transcript !== this._pendingCanonicalText) {
            callbacks.onPlaybackIntegrityError(this._pendingCanonicalText, transcript)
          }
          this._pendingCanonicalText = null
        }
        break
      }

      case 'error':
        if (typeof parsed['message'] === 'string') {
          callbacks.onError(parsed['message'])
        } else {
          callbacks.onError('Provider error')
        }
        break

      // Unknown event types are silently ignored for forward compatibility
      default:
        break
    }
  }

  private _playRemoteAudio(stream: MediaStream): void {
    if (typeof document === 'undefined') return
    const audio = document.createElement('audio')
    audio.srcObject = stream
    audio.autoplay = true
    this._audioElement = audio
  }
}
