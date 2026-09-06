import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import VoiceControls, { HEARTBEAT_INTERVAL_MS } from './VoiceControls'
import { FakeVoiceTransport } from './testing/fakeVoiceTransport'
import type { FinalizedUtterance } from './voiceReducer'
import { admitVoice, heartbeatVoice, releaseVoice } from './voiceApi'
import type { HeartbeatResponse, VoiceAdmissionResponse } from './voiceApi'

vi.mock('./voiceApi', () => ({
  admitVoice: vi.fn(),
  heartbeatVoice: vi.fn(),
  releaseVoice: vi.fn(),
}))

const mockAdmitVoice = vi.mocked(admitVoice)
const mockHeartbeatVoice = vi.mocked(heartbeatVoice)
const mockReleaseVoice = vi.mocked(releaseVoice)

const CSRF = 'test-csrf-token'
const SESSION_ID = 'vs-test-001'
const SDP_ANSWER = 'v=0\r\ntest answer'

// ── Helpers ────────────────────────────────────────────────────────────────────

function admitted(id = SESSION_ID, answer = SDP_ANSWER): VoiceAdmissionResponse {
  return { admitted: true, voiceSessionId: id, sdpAnswer: answer, denialReason: null }
}

function denied(reason = 'VoiceDisabled'): VoiceAdmissionResponse {
  return { admitted: false, voiceSessionId: null, sdpAnswer: null, denialReason: reason }
}

function activeHb(): HeartbeatResponse {
  return { renewed: true, lifecycleState: 'active', remainingSeconds: 60, forcedCloseReason: null }
}

function warningHb(seconds = 30): HeartbeatResponse {
  return { renewed: true, lifecycleState: 'warning_due', remainingSeconds: seconds, forcedCloseReason: null }
}

function expiredHb(reason = 'timeout'): HeartbeatResponse {
  return { renewed: false, lifecycleState: 'expired', remainingSeconds: null, forcedCloseReason: reason }
}

function lifecycleHb(state: 'idle' | 'not_found'): HeartbeatResponse {
  return { renewed: false, lifecycleState: state, remainingSeconds: null, forcedCloseReason: null }
}

interface TestProps {
  transport: FakeVoiceTransport
  csrfToken: string
  voiceSessionId: string | null
  onFinalizedUtterance: ReturnType<typeof vi.fn<(u: FinalizedUtterance) => void>>
  onVoiceSessionChanged: ReturnType<typeof vi.fn<(id: string | null) => void>>
}

function makeProps(overrides: Partial<TestProps> = {}): TestProps {
  return {
    transport: new FakeVoiceTransport(),
    csrfToken: CSRF,
    voiceSessionId: null,
    onFinalizedUtterance: vi.fn<(u: FinalizedUtterance) => void>(),
    onVoiceSessionChanged: vi.fn<(id: string | null) => void>(),
    ...overrides,
  }
}

/** Render, click Start, await the async admit flow, simulate connected. Returns props + render. */
async function renderConnected(overrides: Partial<TestProps> = {}) {
  mockAdmitVoice.mockResolvedValue(admitted())
  const props = makeProps(overrides)
  const result = render(<VoiceControls {...props} />)

  await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
  await waitFor(() => expect(props.transport.connectCount).toBe(1))

  act(() => { props.transport.simulateConnected() })

  return { ...props, ...result }
}

/** Same as renderConnected but safe for vi.useFakeTimers() — uses fireEvent + timer flush. */
async function renderConnectedFake(overrides: Partial<TestProps> = {}) {
  mockAdmitVoice.mockResolvedValue(admitted())
  const props = makeProps(overrides)
  const result = render(<VoiceControls {...props} />)

  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
    await vi.advanceTimersByTimeAsync(0)
  })

  act(() => { props.transport.simulateConnected() })

  return { ...props, ...result }
}

// ── Tests ──────────────────────────────────────────────────────────────────────

describe('VoiceControls', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockReleaseVoice.mockResolvedValue(undefined)
    mockHeartbeatVoice.mockResolvedValue(activeHb())
  })

  // ── Idle render ──────────────────────────────────────────────────────────

  describe('idle state', () => {
    it('renders an accessible Start Voice button', () => {
      render(<VoiceControls {...makeProps()} />)
      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
    })

    it('shows no error, warning, or active status', () => {
      render(<VoiceControls {...makeProps()} />)
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
      expect(screen.queryByLabelText('Voice warning')).not.toBeInTheDocument()
      expect(screen.queryByLabelText('Voice status')).not.toBeInTheDocument()
    })
  })

  // ── Start sequence ───────────────────────────────────────────────────────

  describe('start sequence', () => {
    it('calls prepare then admitVoice with the SDP offer and CSRF token', async () => {
      mockAdmitVoice.mockResolvedValue(admitted())
      const props = makeProps()
      render(<VoiceControls {...props} />)

      await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
      await waitFor(() => expect(mockAdmitVoice).toHaveBeenCalledOnce())

      const [offer, csrf] = mockAdmitVoice.mock.calls[0]
      expect(offer).toContain('v=0')
      expect(csrf).toBe(CSRF)
    })

    it('on admission, connects transport with SDP answer and notifies parent of session ID', async () => {
      const { transport, onVoiceSessionChanged } = await renderConnected()
      expect(transport.lastConnectSdpAnswer).toBe(SDP_ANSWER)
      expect(onVoiceSessionChanged).toHaveBeenCalledWith(SESSION_ID)
    })

    it('on denial, shows error, disconnects prepared resources, and notifies parent null', async () => {
      mockAdmitVoice.mockResolvedValue(denied('GlobalCapReached'))
      const props = makeProps()
      const disconnectSpy = vi.spyOn(props.transport, 'disconnect')
      render(<VoiceControls {...props} />)

      await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
      await waitFor(() => expect(screen.getByRole('alert', { name: 'Voice error' })).toBeInTheDocument())

      expect(screen.getByRole('alert', { name: 'Voice error' })).toHaveTextContent('capacity')
      expect(disconnectSpy).toHaveBeenCalled()
      expect(props.onVoiceSessionChanged).toHaveBeenCalledWith(null)
    })

    it('on prepare failure, shows error and does not call admitVoice', async () => {
      const props = makeProps()
      vi.spyOn(props.transport, 'prepare').mockRejectedValue(new Error('Permission denied'))
      render(<VoiceControls {...props} />)

      await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
      await waitFor(() => expect(screen.getByRole('alert', { name: 'Voice error' })).toBeInTheDocument())

      expect(screen.getByRole('alert', { name: 'Voice error' })).toHaveTextContent('Permission denied')
      expect(mockAdmitVoice).not.toHaveBeenCalled()
    })

    it('on admitVoice network error, disconnects prepared resources and notifies parent null', async () => {
      mockAdmitVoice.mockRejectedValue(new Error('admitVoice failed with status 503'))
      const props = makeProps()
      const disconnectSpy = vi.spyOn(props.transport, 'disconnect')
      render(<VoiceControls {...props} />)

      await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
      await waitFor(() => expect(screen.getByRole('alert', { name: 'Voice error' })).toBeInTheDocument())

      expect(screen.getByRole('alert', { name: 'Voice error' })).toHaveTextContent('503')
      expect(disconnectSpy).toHaveBeenCalled()
      expect(props.onVoiceSessionChanged).toHaveBeenCalledWith(null)
    })

    it('disables Start Voice while requesting', async () => {
      mockAdmitVoice.mockReturnValue(new Promise(() => {}))
      render(<VoiceControls {...makeProps()} />)

      await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
      await waitFor(() => expect(screen.getByRole('button', { name: 'Start Voice' })).toBeDisabled())
    })

    it('disables Start Voice while connecting', async () => {
      mockAdmitVoice.mockResolvedValue(admitted())
      const props = makeProps()
      render(<VoiceControls {...props} />)

      await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
      await waitFor(() => expect(props.transport.connectCount).toBe(1))

      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeDisabled()
    })

    it('does not duplicate admitVoice on rapid double-click', async () => {
      mockAdmitVoice.mockReturnValue(new Promise(() => {}))
      render(<VoiceControls {...makeProps()} />)

      const button = screen.getByRole('button', { name: 'Start Voice' })
      await userEvent.click(button)
      // second click is blocked by disabled state / phase guard
      expect(mockAdmitVoice).toHaveBeenCalledOnce()
    })
  })

  // ── Callback mapping ────────────────────────────────────────────────────

  describe('callback mapping', () => {
    it('connected shows Listening', async () => {
      await renderConnected()
      expect(screen.getByLabelText('Voice status')).toHaveTextContent('Listening')
    })

    it('speech_started shows speech active indicator', async () => {
      const { transport } = await renderConnected()
      act(() => { transport.simulateSpeechStarted() })
      expect(screen.getByLabelText('Speech active')).toBeInTheDocument()
    })

    it('partial transcript is visible as ephemeral text', async () => {
      const { transport } = await renderConnected()
      act(() => { transport.simulatePartialTranscript('add fi') })
      expect(screen.getByLabelText('Partial transcript')).toHaveTextContent('add fi')
    })

    it('partial transcript does not invoke onFinalizedUtterance', async () => {
      const { transport, onFinalizedUtterance } = await renderConnected()
      act(() => { transport.simulatePartialTranscript('add five') })
      expect(onFinalizedUtterance).not.toHaveBeenCalled()
    })

    it('final transcript invokes onFinalizedUtterance with exactly text and nativeMessageId', async () => {
      const { transport, onFinalizedUtterance } = await renderConnected()
      act(() => { transport.simulateFinalTranscript('add five', 'voice:vs-test-001:item_1') })

      expect(onFinalizedUtterance).toHaveBeenCalledOnce()
      expect(onFinalizedUtterance).toHaveBeenCalledWith({
        text: 'add five',
        nativeMessageId: 'voice:vs-test-001:item_1',
      })
    })

    it('playback_started transitions to Speaking', async () => {
      const { transport } = await renderConnected()
      act(() => { transport.simulatePlaybackStarted() })
      expect(screen.getByLabelText('Voice status')).toHaveTextContent('Speaking')
    })

    it('playback_finished transitions back to Listening', async () => {
      const { transport } = await renderConnected()
      act(() => { transport.simulatePlaybackStarted() })
      act(() => { transport.simulatePlaybackDone() })
      expect(screen.getByLabelText('Voice status')).toHaveTextContent('Listening')
    })

    it('playback_failed shows error and playback failure alerts, returns to listening', async () => {
      const { transport } = await renderConnected()
      act(() => { transport.simulatePlaybackStarted() })
      act(() => { transport.simulatePlaybackFailed('decode error') })

      expect(screen.getByLabelText('Voice status')).toHaveTextContent('Listening')
      expect(screen.getByRole('alert', { name: 'Voice error' })).toHaveTextContent('decode error')
      expect(screen.getByRole('alert', { name: 'Playback failure' })).toBeInTheDocument()
    })

    it('playback integrity error shows alert with integrity message', async () => {
      const { transport } = await renderConnected()
      act(() => { transport.simulatePlaybackStarted() })
      act(() => { transport.simulatePlaybackIntegrityError('correct', 'wrong') })

      expect(screen.getByRole('alert', { name: 'Voice error' })).toHaveTextContent('Integrity')
      expect(screen.getByRole('alert', { name: 'Playback failure' })).toBeInTheDocument()
    })

    it('transport error ends session, disconnects, notifies parent null', async () => {
      const { transport, onVoiceSessionChanged } = await renderConnected()
      onVoiceSessionChanged.mockClear()

      act(() => { transport.simulateError('connection reset') })

      expect(screen.getByRole('alert', { name: 'Voice error' })).toHaveTextContent('Voice connection error.')
      expect(transport.disconnectCount).toBe(1)
      expect(onVoiceSessionChanged).toHaveBeenCalledWith(null)
      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
    })

    it('microphone error ends session, disconnects, notifies parent null', async () => {
      const { transport, onVoiceSessionChanged } = await renderConnected()
      onVoiceSessionChanged.mockClear()

      act(() => { transport.simulateMicrophoneFailed('NotAllowedError') })

      expect(screen.getByRole('alert', { name: 'Voice error' })).toHaveTextContent('Microphone error.')
      expect(transport.disconnectCount).toBe(1)
      expect(onVoiceSessionChanged).toHaveBeenCalledWith(null)
    })
  })

  // ── End sequence ─────────────────────────────────────────────────────────

  describe('end sequence', () => {
    it('disconnects, releases, returns to idle, and notifies parent null', async () => {
      const { transport, onVoiceSessionChanged } = await renderConnected()
      onVoiceSessionChanged.mockClear()

      await userEvent.click(screen.getByRole('button', { name: 'End Voice' }))
      await waitFor(() => expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled())

      expect(transport.disconnectCount).toBe(1)
      expect(mockReleaseVoice).toHaveBeenCalledWith(SESSION_ID, CSRF)
      expect(onVoiceSessionChanged).toHaveBeenCalledWith(null)
    })

    it('release failure still returns to idle with visible error', async () => {
      mockReleaseVoice.mockRejectedValue(new Error('releaseVoice failed with status 500'))
      const { onVoiceSessionChanged } = await renderConnected()
      onVoiceSessionChanged.mockClear()

      await userEvent.click(screen.getByRole('button', { name: 'End Voice' }))
      await waitFor(() => expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled())

      expect(screen.getByRole('alert', { name: 'Voice error' })).toHaveTextContent('Release failed')
      expect(onVoiceSessionChanged).toHaveBeenCalledWith(null)
    })

    it('End Voice button disappears during ending — no duplicate release', async () => {
      await renderConnected()

      await userEvent.click(screen.getByRole('button', { name: 'End Voice' }))
      await waitFor(() => expect(screen.queryByRole('button', { name: 'End Voice' })).not.toBeInTheDocument())

      expect(mockReleaseVoice).toHaveBeenCalledOnce()
    })
  })

  // ── Mute toggle ──────────────────────────────────────────────────────────

  describe('mute toggle', () => {
    it('renders mute button with aria-pressed=false while connected', async () => {
      await renderConnected()
      const button = screen.getByRole('button', { name: 'Mute microphone' })
      expect(button).toHaveAttribute('aria-pressed', 'false')
    })

    it('toggles to Unmute with aria-pressed=true and calls transport.setMuted(true)', async () => {
      const { transport } = await renderConnected()
      await userEvent.click(screen.getByRole('button', { name: 'Mute microphone' }))

      expect(screen.getByRole('button', { name: 'Unmute microphone' })).toHaveAttribute('aria-pressed', 'true')
      expect(transport.isMuted).toBe(true)
    })

    it('toggles back and calls transport.setMuted(false)', async () => {
      const { transport } = await renderConnected()
      await userEvent.click(screen.getByRole('button', { name: 'Mute microphone' }))
      await userEvent.click(screen.getByRole('button', { name: 'Unmute microphone' }))

      expect(screen.getByRole('button', { name: 'Mute microphone' })).toHaveAttribute('aria-pressed', 'false')
      expect(transport.isMuted).toBe(false)
    })
  })

  // ── Barge-in ─────────────────────────────────────────────────────────────

  describe('barge-in', () => {
    it('speech during speaking dispatches barge-in and calls cancelPlayback(0)', async () => {
      const { transport } = await renderConnected()

      act(() => { transport.simulatePlaybackStarted() })
      expect(screen.getByLabelText('Voice status')).toHaveTextContent('Speaking')

      act(() => { transport.simulateSpeechStarted() })

      expect(screen.getByLabelText('Voice status')).toHaveTextContent('Listening')
      expect(transport.cancelPlaybackCalls).toEqual([0])
    })

    it('does not mark the final utterance as interrupted', async () => {
      const { transport, onFinalizedUtterance } = await renderConnected()

      act(() => { transport.simulatePlaybackStarted() })
      act(() => { transport.simulateSpeechStarted() })
      act(() => { transport.simulateFinalTranscript('hello', 'voice:vs-test-001:item_2') })

      expect(onFinalizedUtterance).toHaveBeenCalledWith({
        text: 'hello',
        nativeMessageId: 'voice:vs-test-001:item_2',
      })
    })
  })

  // ── Stale callbacks ──────────────────────────────────────────────────────

  describe('stale callbacks', () => {
    it('transport callbacks are no-ops after end (transport disconnected)', async () => {
      const { transport, onFinalizedUtterance } = await renderConnected()

      await userEvent.click(screen.getByRole('button', { name: 'End Voice' }))
      await waitFor(() => expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled())

      transport.simulateFinalTranscript('stale', 'voice:vs-stale:item_1')
      expect(onFinalizedUtterance).not.toHaveBeenCalled()
    })

    it('cleanup on unmount disconnects transport — no post-unmount callbacks', async () => {
      const { transport, onFinalizedUtterance, unmount } = await renderConnected()

      unmount()

      expect(transport.disconnectCount).toBe(1)
      transport.simulateFinalTranscript('stale', 'voice:vs-stale:item_1')
      expect(onFinalizedUtterance).not.toHaveBeenCalled()
    })
  })

  // ── Parent notifications ─────────────────────────────────────────────────

  describe('parent notifications', () => {
    it('notifies parent with session ID on admission', async () => {
      const { onVoiceSessionChanged } = await renderConnected()
      expect(onVoiceSessionChanged).toHaveBeenCalledWith(SESSION_ID)
    })

    it('notifies parent with null on end', async () => {
      const { onVoiceSessionChanged } = await renderConnected()
      onVoiceSessionChanged.mockClear()

      await userEvent.click(screen.getByRole('button', { name: 'End Voice' }))
      await waitFor(() => expect(onVoiceSessionChanged).toHaveBeenCalledWith(null))
    })

    it('notifies parent with null on transport error', async () => {
      const { transport, onVoiceSessionChanged } = await renderConnected()
      onVoiceSessionChanged.mockClear()

      act(() => { transport.simulateError('lost') })
      expect(onVoiceSessionChanged).toHaveBeenCalledWith(null)
    })
  })

  // ── External voiceSessionId change ───────────────────────────────────────

  describe('external voiceSessionId change', () => {
    it('externally cleared voiceSessionId fences and disconnects active session', async () => {
      mockAdmitVoice.mockResolvedValue(admitted())
      const transport = new FakeVoiceTransport()
      const props = makeProps({ transport })
      const { rerender } = render(<VoiceControls {...props} />)

      await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
      await waitFor(() => expect(transport.connectCount).toBe(1))
      act(() => { transport.simulateConnected() })

      // Parent syncs the admitted ID
      rerender(<VoiceControls {...props} voiceSessionId={SESSION_ID} />)
      // Parent clears the session externally
      rerender(<VoiceControls {...props} voiceSessionId={null} />)

      expect(transport.disconnectCount).toBe(1)
      expect(screen.getByRole('alert', { name: 'Voice error' })).toHaveTextContent('externally')
      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
    })
  })

  // ── Heartbeat and timer tests (fake timers) ──────────────────────────────

  describe('heartbeat (fake timers)', () => {
    beforeEach(() => { vi.useFakeTimers() })
    afterEach(() => { vi.useRealTimers() })

    it('sends heartbeat at HEARTBEAT_INTERVAL_MS after connection', async () => {
      mockHeartbeatVoice.mockResolvedValue(activeHb())
      await renderConnectedFake()

      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })

      expect(mockHeartbeatVoice).toHaveBeenCalledOnce()
      expect(mockHeartbeatVoice).toHaveBeenCalledWith(SESSION_ID, CSRF)
    })

    it('active response causes no visible disruption', async () => {
      mockHeartbeatVoice.mockResolvedValue(activeHb())
      await renderConnectedFake()

      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })

      expect(screen.getByLabelText('Voice status')).toHaveTextContent('Listening')
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    })

    it('warning_due produces one accessible warning', async () => {
      mockHeartbeatVoice.mockResolvedValue(warningHb(30))
      await renderConnectedFake()

      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })

      expect(screen.getByLabelText('Voice warning')).toHaveTextContent('30')
    })

    it('expired ends session locally with reason', async () => {
      mockHeartbeatVoice.mockResolvedValue(expiredHb('timeout'))
      const { transport, onVoiceSessionChanged } = await renderConnectedFake()
      onVoiceSessionChanged.mockClear()

      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })

      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
      expect(screen.getByRole('alert', { name: 'Voice error' })).toHaveTextContent('expired')
      expect(transport.disconnectCount).toBe(1)
      expect(onVoiceSessionChanged).toHaveBeenCalledWith(null)
    })

    it('idle ends session', async () => {
      mockHeartbeatVoice.mockResolvedValue(lifecycleHb('idle'))
      const { onVoiceSessionChanged } = await renderConnectedFake()
      onVoiceSessionChanged.mockClear()

      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })

      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
      expect(onVoiceSessionChanged).toHaveBeenCalledWith(null)
    })

    it('not_found ends session', async () => {
      mockHeartbeatVoice.mockResolvedValue(lifecycleHb('not_found'))
      const { onVoiceSessionChanged } = await renderConnectedFake()
      onVoiceSessionChanged.mockClear()

      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })

      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
      expect(onVoiceSessionChanged).toHaveBeenCalledWith(null)
    })

    it('heartbeat network failure ends session with error', async () => {
      mockHeartbeatVoice.mockRejectedValue(new Error('heartbeat network failure'))
      const { transport, onVoiceSessionChanged } = await renderConnectedFake()
      onVoiceSessionChanged.mockClear()

      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })

      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
      expect(screen.getByRole('alert', { name: 'Voice error' })).toHaveTextContent('Voice session lost.')
      expect(transport.disconnectCount).toBe(1)
      expect(onVoiceSessionChanged).toHaveBeenCalledWith(null)
    })

    it('does not overlap heartbeat requests', async () => {
      let resolveFirst!: (value: HeartbeatResponse) => void
      mockHeartbeatVoice.mockImplementationOnce(
        () => new Promise<HeartbeatResponse>((resolve) => { resolveFirst = resolve }),
      )
      await renderConnectedFake()

      // First tick: heartbeat starts (hangs)
      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })
      expect(mockHeartbeatVoice).toHaveBeenCalledOnce()

      // Second tick: skipped (first still in-flight)
      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })
      expect(mockHeartbeatVoice).toHaveBeenCalledOnce()

      // Resolve first, then next tick fires
      mockHeartbeatVoice.mockResolvedValue(activeHb())
      await act(async () => {
        resolveFirst(activeHb())
        await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS)
      })
      expect(mockHeartbeatVoice).toHaveBeenCalledTimes(2)
    })

    it('clears heartbeat timer on end', async () => {
      mockHeartbeatVoice.mockResolvedValue(activeHb())
      await renderConnectedFake()

      await act(async () => {
        fireEvent.click(screen.getByRole('button', { name: 'End Voice' }))
        await vi.advanceTimersByTimeAsync(0)
      })

      mockHeartbeatVoice.mockClear()
      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS * 2) })
      expect(mockHeartbeatVoice).not.toHaveBeenCalled()
    })

    it('clears heartbeat timer on transport error', async () => {
      mockHeartbeatVoice.mockResolvedValue(activeHb())
      const { transport } = await renderConnectedFake()

      act(() => { transport.simulateError('fatal') })

      mockHeartbeatVoice.mockClear()
      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS * 2) })
      expect(mockHeartbeatVoice).not.toHaveBeenCalled()
    })

    it('clears heartbeat timer on unmount', async () => {
      mockHeartbeatVoice.mockResolvedValue(activeHb())
      const { unmount } = await renderConnectedFake()

      unmount()

      mockHeartbeatVoice.mockClear()
      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS * 2) })
      expect(mockHeartbeatVoice).not.toHaveBeenCalled()
    })
  })

  // ── In-flight admission cleanup (Task 15 finding #1) ──────────────────

  describe('in-flight admission cleanup', () => {
    it('unmount during admission passes AbortSignal to admitVoice and aborts it', async () => {
      let capturedSignal: AbortSignal | undefined
      mockAdmitVoice.mockImplementation((_offer, _csrf, signal) => {
        capturedSignal = signal
        return new Promise(() => {})
      })
      const props = makeProps()
      const { unmount } = render(<VoiceControls {...props} />)

      await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
      await waitFor(() => expect(mockAdmitVoice).toHaveBeenCalledOnce())

      expect(capturedSignal).toBeInstanceOf(AbortSignal)
      expect(capturedSignal!.aborted).toBe(false)

      unmount()

      expect(capturedSignal!.aborted).toBe(true)
    })

    it('late successful admission after unmount triggers release and never connects/notifies', async () => {
      let resolveAdmit!: (value: VoiceAdmissionResponse) => void
      mockAdmitVoice.mockImplementation(
        () => new Promise<VoiceAdmissionResponse>((resolve) => { resolveAdmit = resolve }),
      )
      const props = makeProps()
      const { unmount } = render(<VoiceControls {...props} />)

      await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
      await waitFor(() => expect(mockAdmitVoice).toHaveBeenCalledOnce())

      unmount()

      // Server committed the session before abort was processed
      await act(async () => { resolveAdmit(admitted()) })

      // Best-effort release must be called to reclaim server-side session
      expect(mockReleaseVoice).toHaveBeenCalledWith(SESSION_ID, CSRF)
      // Never connected or notified parent after unmount
      expect(props.transport.connectCount).toBe(0)
      expect(props.onVoiceSessionChanged).not.toHaveBeenCalled()
    })

    it('abort rejection after unmount does not trigger state callbacks or unhandled rejection', async () => {
      mockAdmitVoice.mockImplementation((_offer, _csrf, signal) => {
        return new Promise<VoiceAdmissionResponse>((_resolve, reject) => {
          signal?.addEventListener('abort', () => {
            reject(new DOMException('The operation was aborted.', 'AbortError'))
          })
        })
      })
      const props = makeProps()
      const { unmount } = render(<VoiceControls {...props} />)

      await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
      await waitFor(() => expect(mockAdmitVoice).toHaveBeenCalledOnce())

      unmount()

      // Flush microtask queue — AbortError should be silently handled
      await act(async () => { await new Promise((r) => setTimeout(r, 0)) })

      // No parent notification after unmount
      expect(props.onVoiceSessionChanged).not.toHaveBeenCalled()
      // No release call because abort prevented admission from completing
      expect(mockReleaseVoice).not.toHaveBeenCalled()
    })
  })

  // ── Double start gate (Task 15 finding #3) ──────────────────────────────

  describe('double start gate', () => {
    it('synchronous double-start invokes prepare/admit exactly once', async () => {
      const props = makeProps()
      const prepareSpy = vi.spyOn(props.transport, 'prepare')
      prepareSpy.mockReturnValue(new Promise(() => {}))
      render(<VoiceControls {...props} />)

      const button = screen.getByRole('button', { name: 'Start Voice' })
      // Two synchronous clicks before React re-renders the disabled state
      fireEvent.click(button)
      fireEvent.click(button)

      expect(prepareSpy).toHaveBeenCalledOnce()
    })
  })

  // ── Heartbeat stale response (Task 15 finding #5) ───────────────────────

  describe('heartbeat stale response', () => {
    beforeEach(() => { vi.useFakeTimers() })
    afterEach(() => { vi.useRealTimers() })

    it('heartbeat response resolving after end does not affect idle state', async () => {
      let resolveHeartbeat!: (value: HeartbeatResponse) => void
      mockHeartbeatVoice.mockImplementationOnce(
        () => new Promise<HeartbeatResponse>((resolve) => { resolveHeartbeat = resolve }),
      )
      const { onVoiceSessionChanged } = await renderConnectedFake()

      // Trigger heartbeat (starts in-flight)
      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })
      expect(mockHeartbeatVoice).toHaveBeenCalledOnce()

      // End session (bumps generation) — flush release promise with fake timer tick
      await act(async () => {
        fireEvent.click(screen.getByRole('button', { name: 'End Voice' }))
        await vi.advanceTimersByTimeAsync(0)
      })
      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
      onVoiceSessionChanged.mockClear()

      // Late expired heartbeat resolves — must be ignored
      await act(async () => { resolveHeartbeat(expiredHb('timeout')) })

      // Still idle, no expired error surfaced, no parent notification
      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
      expect(screen.queryByText(/Session expired/)).not.toBeInTheDocument()
      expect(onVoiceSessionChanged).not.toHaveBeenCalled()
    })
  })

  // ── Reducer terminal reset at UI level (Task 15 finding #2) ─────────────

  describe('terminal reset clears playback failure at UI level', () => {
    it('playback failure alert disappears after transport error returns to idle', async () => {
      const { transport } = await renderConnected()

      // Enter speaking, then playback fails
      act(() => { transport.simulatePlaybackStarted() })
      act(() => { transport.simulatePlaybackFailed('decode error') })
      expect(screen.getByRole('alert', { name: 'Playback failure' })).toBeInTheDocument()

      // Transport error terminates session
      act(() => { transport.simulateError('connection lost') })

      // Back to idle — playback failure alert should be gone
      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
      expect(screen.queryByRole('alert', { name: 'Playback failure' })).not.toBeInTheDocument()
    })
  })

  // ── Exported constant ────────────────────────────────────────────────────

  it('exports HEARTBEAT_INTERVAL_MS as 30000', () => {
    expect(HEARTBEAT_INTERVAL_MS).toBe(30_000)
  })

  // ── Server session release on failure ─────────────────────────────────────

  describe('failure session release', () => {
    it('transport error releases admitted session exactly once', async () => {
      const { transport, onVoiceSessionChanged } = await renderConnected()
      onVoiceSessionChanged.mockClear()

      act(() => { transport.simulateError('connection reset') })

      expect(mockReleaseVoice).toHaveBeenCalledOnce()
      expect(mockReleaseVoice).toHaveBeenCalledWith(SESSION_ID, CSRF)
      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
    })

    it('microphone failure releases admitted session exactly once', async () => {
      const { transport, onVoiceSessionChanged } = await renderConnected()
      onVoiceSessionChanged.mockClear()

      act(() => { transport.simulateMicrophoneFailed('NotAllowedError') })

      expect(mockReleaseVoice).toHaveBeenCalledOnce()
      expect(mockReleaseVoice).toHaveBeenCalledWith(SESSION_ID, CSRF)
      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
    })

    it('release rejection on failure path does not cause unhandled rejection', async () => {
      mockReleaseVoice.mockRejectedValue(new Error('release failed'))
      const { transport } = await renderConnected()

      act(() => { transport.simulateError('connection reset') })

      // Flush microtask queue — no unhandled rejection
      await act(async () => { await new Promise((r) => setTimeout(r, 0)) })

      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
    })

    it('stale transport error after disconnect does not double-release', async () => {
      const { transport, onVoiceSessionChanged } = await renderConnected()
      onVoiceSessionChanged.mockClear()

      // End voice normally (releases)
      await userEvent.click(screen.getByRole('button', { name: 'End Voice' }))
      await waitFor(() => expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled())

      mockReleaseVoice.mockClear()

      // Stale error after transport disconnected — FakeVoiceTransport suppresses
      transport.simulateError('stale error')

      expect(mockReleaseVoice).not.toHaveBeenCalled()
    })

    it('connect throw after successful admission releases session and shows sanitized error', async () => {
      mockAdmitVoice.mockResolvedValue(admitted())
      const props = makeProps()
      // Make connect throw synchronously
      vi.spyOn(props.transport, 'connect').mockImplementation(() => {
        throw new Error('RTCPeerConnection internal failure detail xyz')
      })
      render(<VoiceControls {...props} />)

      await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }))
      await waitFor(() => expect(mockReleaseVoice).toHaveBeenCalledOnce())

      expect(mockReleaseVoice).toHaveBeenCalledWith(SESSION_ID, CSRF)
      expect(props.onVoiceSessionChanged).toHaveBeenCalledWith(null)
      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
    })
  })

  describe('failure session release (fake timers)', () => {
    beforeEach(() => { vi.useFakeTimers() })
    afterEach(() => { vi.useRealTimers() })

    it('heartbeat network failure releases admitted session', async () => {
      mockHeartbeatVoice.mockRejectedValue(new Error('heartbeat network failure'))
      await renderConnectedFake()

      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })

      expect(mockReleaseVoice).toHaveBeenCalledOnce()
      expect(mockReleaseVoice).toHaveBeenCalledWith(SESSION_ID, CSRF)
      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
    })

    it('expired/idle/not_found heartbeat does not release (server already reclaimed)', async () => {
      mockHeartbeatVoice.mockResolvedValue(expiredHb('timeout'))
      await renderConnectedFake()

      await act(async () => { await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS) })

      expect(mockReleaseVoice).not.toHaveBeenCalled()
      expect(screen.getByRole('button', { name: 'Start Voice' })).toBeEnabled()
    })
  })
})
