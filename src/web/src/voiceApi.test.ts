import { describe, expect, it, vi } from 'vitest'

import { admitVoice, heartbeatVoice, releaseVoice } from './voiceApi'

// ── Helpers ───────────────────────────────────────────────────────────────────

function stubFetch(resolvedValue: Partial<Response>): ReturnType<typeof vi.fn> {
  const mock = vi.fn().mockResolvedValue(resolvedValue)
  vi.stubGlobal('fetch', mock)
  return mock
}

function stubFetchOkJson(body: unknown): ReturnType<typeof vi.fn> {
  return stubFetch({
    ok: true,
    status: 200,
    json: () => Promise.resolve(body),
  } as Response)
}

function stubFetchStatus(status: number): ReturnType<typeof vi.fn> {
  return stubFetch({
    ok: status >= 200 && status < 300,
    status,
  } as Response)
}

function stubFetchRejected(error: Error): ReturnType<typeof vi.fn> {
  const mock = vi.fn().mockRejectedValue(error)
  vi.stubGlobal('fetch', mock)
  return mock
}

const CSRF = 'test-csrf-token'
const SDP_OFFER = 'v=0\r\nsdp offer content'
const SESSION_ID = 'vs-9f7e4c2a-0001'

// ── admitVoice ────────────────────────────────────────────────────────────────

describe('admitVoice', () => {
  it('posts to /api/voice/admit with credentials, JSON content-type, CSRF header, and exact body', async () => {
    const mock = stubFetchOkJson({ admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\nanswer' })

    await admitVoice(SDP_OFFER, CSRF)

    expect(mock).toHaveBeenCalledOnce()
    const [url, init] = mock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/voice/admit')
    expect(init.method).toBe('POST')
    expect(init.credentials).toBe('include')
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json')
    expect((init.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe(CSRF)
    expect(JSON.parse(init.body as string)).toEqual({ sdpOffer: SDP_OFFER })
  })

  it('returns a parsed admitted response with null denialReason', async () => {
    stubFetchOkJson({ admitted: true, voiceSessionId: 'vs-abc', sdpAnswer: 'v=0\r\nanswer' })

    const result = await admitVoice(SDP_OFFER, CSRF)

    expect(result.admitted).toBe(true)
    expect(result.voiceSessionId).toBe('vs-abc')
    expect(result.sdpAnswer).toBe('v=0\r\nanswer')
    expect(result.denialReason).toBeNull()
  })

  it('returns a parsed denied response with null voiceSessionId and sdpAnswer', async () => {
    stubFetchOkJson({ admitted: false, denialReason: 'VoiceDisabled' })

    const result = await admitVoice(SDP_OFFER, CSRF)

    expect(result.admitted).toBe(false)
    expect(result.voiceSessionId).toBeNull()
    expect(result.sdpAnswer).toBeNull()
    expect(result.denialReason).toBe('VoiceDisabled')
  })

  it('accepts all known denial reasons', async () => {
    for (const reason of ['VoiceDisabled', 'AlreadyActive', 'GlobalCapReached'] as const) {
      stubFetchOkJson({ admitted: false, denialReason: reason })
      const result = await admitVoice(SDP_OFFER, CSRF)
      expect(result.denialReason).toBe(reason)
    }
  })

  it('accepts a forward-compatible unknown denial reason string', async () => {
    stubFetchOkJson({ admitted: false, denialReason: 'FutureReasonNotYetKnown' })

    const result = await admitVoice(SDP_OFFER, CSRF)

    expect(result.admitted).toBe(false)
    expect(result.denialReason).toBe('FutureReasonNotYetKnown')
  })

  it('throws on non-ok response; error contains operation name and status', async () => {
    stubFetchStatus(503)

    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow('admitVoice')
    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow('503')
  })

  it('does not include response body in the non-ok error message', async () => {
    stubFetch({
      ok: false,
      status: 503,
      json: () => Promise.resolve({ error: 'do-not-leak-this-server-secret' }),
    } as Response)

    let thrown: unknown
    try {
      await admitVoice(SDP_OFFER, CSRF)
    } catch (e) {
      thrown = e
    }
    expect((thrown as Error).message).not.toContain('do-not-leak-this-server-secret')
  })

  it('propagates network errors as-is', async () => {
    stubFetchRejected(new TypeError('Failed to fetch'))

    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow(TypeError)
  })

  it('propagates abort errors as-is', async () => {
    stubFetchRejected(new DOMException('The operation was aborted.', 'AbortError'))
    const controller = new AbortController()

    await expect(admitVoice(SDP_OFFER, CSRF, controller.signal)).rejects.toMatchObject({ name: 'AbortError' })
  })

  it('forwards the abort signal to fetch', async () => {
    const mock = stubFetchOkJson({ admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0' })
    const controller = new AbortController()

    await admitVoice(SDP_OFFER, CSRF, controller.signal)

    const [, init] = mock.mock.calls[0] as [string, RequestInit]
    expect(init.signal).toBe(controller.signal)
  })

  it('throws before fetching when csrfToken is blank', async () => {
    const mock = stubFetchOkJson({})
    await expect(admitVoice(SDP_OFFER, '')).rejects.toThrow('csrfToken must not be blank')
    await expect(admitVoice(SDP_OFFER, '   ')).rejects.toThrow('csrfToken must not be blank')
    expect(mock).not.toHaveBeenCalled()
  })

  it('propagates JSON parse errors from the response', async () => {
    stubFetch({
      ok: true,
      status: 200,
      json: () => Promise.reject(new SyntaxError('Unexpected token')),
    } as Response)

    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow(SyntaxError)
  })

  it('throws on non-object response body', async () => {
    stubFetchOkJson('not-an-object')

    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow()
  })

  it('throws when admitted field is missing', async () => {
    stubFetchOkJson({ voiceSessionId: 'vs-1', sdpAnswer: 'v=0' })

    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow('admitted')
  })

  it('throws on impossible shape: admitted=true but voiceSessionId is blank', async () => {
    stubFetchOkJson({ admitted: true, voiceSessionId: '', sdpAnswer: 'v=0' })

    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow('voiceSessionId')
  })

  it('throws on impossible shape: admitted=true but sdpAnswer is missing', async () => {
    stubFetchOkJson({ admitted: true, voiceSessionId: 'vs-1' })

    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow('sdpAnswer')
  })

  it('throws on impossible shape: admitted=true but denialReason is present', async () => {
    stubFetchOkJson({ admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0', denialReason: 'VoiceDisabled' })

    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow('denialReason')
  })

  it('throws on impossible shape: admitted=false but voiceSessionId is present', async () => {
    stubFetchOkJson({ admitted: false, denialReason: 'VoiceDisabled', voiceSessionId: 'vs-1' })

    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow('voiceSessionId')
  })

  it('throws on impossible shape: admitted=false but sdpAnswer is present', async () => {
    stubFetchOkJson({ admitted: false, denialReason: 'VoiceDisabled', sdpAnswer: 'v=0' })

    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow('sdpAnswer')
  })

  it('throws on impossible shape: admitted=false but denialReason is missing', async () => {
    stubFetchOkJson({ admitted: false })

    await expect(admitVoice(SDP_OFFER, CSRF)).rejects.toThrow('denialReason')
  })
})

// ── heartbeatVoice ────────────────────────────────────────────────────────────

describe('heartbeatVoice', () => {
  it('posts to /api/voice/heartbeat with credentials, JSON content-type, CSRF header, and exact body', async () => {
    const mock = stubFetchOkJson({
      renewed: true,
      lifecycleState: 'active',
      remainingSeconds: 60,
      forcedCloseReason: null,
    })

    await heartbeatVoice(SESSION_ID, CSRF)

    expect(mock).toHaveBeenCalledOnce()
    const [url, init] = mock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/voice/heartbeat')
    expect(init.method).toBe('POST')
    expect(init.credentials).toBe('include')
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json')
    expect((init.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe(CSRF)
    expect(JSON.parse(init.body as string)).toEqual({ voiceSessionId: SESSION_ID })
  })

  it('parses a successful active heartbeat response', async () => {
    stubFetchOkJson({ renewed: true, lifecycleState: 'active', remainingSeconds: 60, forcedCloseReason: null })

    const result = await heartbeatVoice(SESSION_ID, CSRF)

    expect(result.renewed).toBe(true)
    expect(result.lifecycleState).toBe('active')
    expect(result.remainingSeconds).toBe(60)
    expect(result.forcedCloseReason).toBeNull()
  })

  it('parses a response with null remainingSeconds', async () => {
    stubFetchOkJson({ renewed: false, lifecycleState: 'expired', remainingSeconds: null, forcedCloseReason: 'timeout' })

    const result = await heartbeatVoice(SESSION_ID, CSRF)

    expect(result.remainingSeconds).toBeNull()
    expect(result.forcedCloseReason).toBe('timeout')
  })

  it.each(['active', 'warning_due', 'expired', 'idle'] as const)(
    'accepts known lifecycle state: %s',
    async (state) => {
      stubFetchOkJson({ renewed: false, lifecycleState: state, remainingSeconds: null, forcedCloseReason: null })

      const result = await heartbeatVoice(SESSION_ID, CSRF)

      expect(result.lifecycleState).toBe(state)
    },
  )

  it('maps 404 to not_found lifecycle state without throwing', async () => {
    stubFetchStatus(404)

    const result = await heartbeatVoice(SESSION_ID, CSRF)

    expect(result.lifecycleState).toBe('not_found')
    expect(result.renewed).toBe(false)
    expect(result.remainingSeconds).toBeNull()
    expect(result.forcedCloseReason).toBeNull()
  })

  it('throws on an unknown lifecycle state', async () => {
    stubFetchOkJson({ renewed: false, lifecycleState: 'unknown_future_state', remainingSeconds: null, forcedCloseReason: null })

    await expect(heartbeatVoice(SESSION_ID, CSRF)).rejects.toThrow('lifecycleState')
  })

  it('throws on invalid remainingSeconds (negative)', async () => {
    stubFetchOkJson({ renewed: true, lifecycleState: 'active', remainingSeconds: -1, forcedCloseReason: null })

    await expect(heartbeatVoice(SESSION_ID, CSRF)).rejects.toThrow('remainingSeconds')
  })

  it('throws on invalid remainingSeconds (non-number)', async () => {
    stubFetchOkJson({ renewed: true, lifecycleState: 'active', remainingSeconds: 'sixty', forcedCloseReason: null })

    await expect(heartbeatVoice(SESSION_ID, CSRF)).rejects.toThrow('remainingSeconds')
  })

  it('throws on non-ok status that is not 404; error contains operation name and status', async () => {
    stubFetchStatus(500)

    await expect(heartbeatVoice(SESSION_ID, CSRF)).rejects.toThrow('heartbeatVoice')
    await expect(heartbeatVoice(SESSION_ID, CSRF)).rejects.toThrow('500')
  })

  it('propagates network errors as-is', async () => {
    stubFetchRejected(new TypeError('Network error'))

    await expect(heartbeatVoice(SESSION_ID, CSRF)).rejects.toThrow(TypeError)
  })

  it('throws before fetching when csrfToken is blank', async () => {
    const mock = stubFetchOkJson({})
    await expect(heartbeatVoice(SESSION_ID, '')).rejects.toThrow('csrfToken must not be blank')
    expect(mock).not.toHaveBeenCalled()
  })

  it('forwards the abort signal to fetch', async () => {
    const mock = stubFetchOkJson({ renewed: true, lifecycleState: 'active', remainingSeconds: 30, forcedCloseReason: null })
    const controller = new AbortController()

    await heartbeatVoice(SESSION_ID, CSRF, controller.signal)

    const [, init] = mock.mock.calls[0] as [string, RequestInit]
    expect(init.signal).toBe(controller.signal)
  })
})

// ── releaseVoice ──────────────────────────────────────────────────────────────

describe('releaseVoice', () => {
  it('posts to /api/voice/release with credentials, JSON content-type, CSRF header, and exact body', async () => {
    const mock = stubFetchStatus(200)

    await releaseVoice(SESSION_ID, CSRF)

    expect(mock).toHaveBeenCalledOnce()
    const [url, init] = mock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/voice/release')
    expect(init.method).toBe('POST')
    expect(init.credentials).toBe('include')
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json')
    expect((init.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe(CSRF)
    expect(JSON.parse(init.body as string)).toEqual({ voiceSessionId: SESSION_ID })
  })

  it('returns void on success without reading the response body', async () => {
    const json = vi.fn()
    stubFetch({ ok: true, status: 200, json } as unknown as Response)

    const result = await releaseVoice(SESSION_ID, CSRF)

    expect(result).toBeUndefined()
    expect(json).not.toHaveBeenCalled()
  })

  it('throws on non-ok response; error contains operation name and status', async () => {
    stubFetchStatus(403)

    await expect(releaseVoice(SESSION_ID, CSRF)).rejects.toThrow('releaseVoice')
    await expect(releaseVoice(SESSION_ID, CSRF)).rejects.toThrow('403')
  })

  it('propagates network errors as-is', async () => {
    stubFetchRejected(new TypeError('Network error'))

    await expect(releaseVoice(SESSION_ID, CSRF)).rejects.toThrow(TypeError)
  })

  it('throws before fetching when csrfToken is blank', async () => {
    const mock = stubFetchStatus(200)
    await expect(releaseVoice(SESSION_ID, '')).rejects.toThrow('csrfToken must not be blank')
    expect(mock).not.toHaveBeenCalled()
  })

  it('forwards the abort signal to fetch', async () => {
    const mock = stubFetchStatus(200)
    const controller = new AbortController()

    await releaseVoice(SESSION_ID, CSRF, controller.signal)

    const [, init] = mock.mock.calls[0] as [string, RequestInit]
    expect(init.signal).toBe(controller.signal)
  })
})
