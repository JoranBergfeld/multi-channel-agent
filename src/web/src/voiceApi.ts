/**
 * Typed fetch client for the voice session lifecycle endpoints.
 * No React, DOM, or WebRTC dependencies — exercisable in Node.
 */

// ── Types ─────────────────────────────────────────────────────────────────────

/**
 * Known server-side reasons for an admission denial. String union is open for forward
 * compatibility: the server may introduce new reasons without breaking this client.
 */
export type VoiceAdmissionDenialReason = 'VoiceDisabled' | 'AlreadyActive' | 'GlobalCapReached';

/**
 * Server response for POST /api/voice/admit. The admitted and denied shapes are mutually exclusive:
 * - admitted=true  → voiceSessionId and sdpAnswer are non-blank; denialReason is null
 * - admitted=false → denialReason is non-blank; voiceSessionId and sdpAnswer are null
 */
export interface VoiceAdmissionResponse {
  admitted: boolean;
  voiceSessionId: string | null;
  sdpAnswer: string | null;
  /** A known denial reason or an unrecognized future reason; null on admitted=true. */
  denialReason: VoiceAdmissionDenialReason | string | null;
}

export type HeartbeatLifecycleState = 'active' | 'warning_due' | 'expired' | 'idle' | 'not_found';

/**
 * Server response for POST /api/voice/heartbeat. A 404 response is mapped by this client
 * to lifecycleState 'not_found' rather than thrown as an error.
 */
export interface HeartbeatResponse {
  renewed: boolean;
  lifecycleState: HeartbeatLifecycleState;
  remainingSeconds: number | null;
  forcedCloseReason: string | null;
}

// ── Validation ────────────────────────────────────────────────────────────────

const KNOWN_LIFECYCLE_STATES = new Set<string>(['active', 'warning_due', 'expired', 'idle', 'not_found']);

function fail(msg: string): never {
  throw new Error(`Voice API response validation failed: ${msg}`);
}

function isNullish(value: unknown): value is null | undefined {
  return value === null || value === undefined;
}

function parseAdmissionResponse(raw: unknown): VoiceAdmissionResponse {
  if (typeof raw !== 'object' || raw === null) {
    fail('expected an object');
  }

  const r = raw as Record<string, unknown>;

  if (typeof r['admitted'] !== 'boolean') {
    fail("'admitted' must be a boolean");
  }

  if (r['admitted'] === true) {
    if (typeof r['voiceSessionId'] !== 'string' || r['voiceSessionId'].trim() === '') {
      fail("admitted response must have a non-blank 'voiceSessionId'");
    }
    if (typeof r['sdpAnswer'] !== 'string' || r['sdpAnswer'].trim() === '') {
      fail("admitted response must have a non-blank 'sdpAnswer'");
    }
    if (!isNullish(r['denialReason'])) {
      fail("admitted response must not have 'denialReason'");
    }
    return {
      admitted: true,
      voiceSessionId: r['voiceSessionId'],
      sdpAnswer: r['sdpAnswer'],
      denialReason: null,
    };
  } else {
    if (typeof r['denialReason'] !== 'string' || r['denialReason'].trim() === '') {
      fail("denied response must have a non-blank 'denialReason'");
    }
    if (!isNullish(r['voiceSessionId'])) {
      fail("denied response must not have 'voiceSessionId'");
    }
    if (!isNullish(r['sdpAnswer'])) {
      fail("denied response must not have 'sdpAnswer'");
    }
    return {
      admitted: false,
      voiceSessionId: null,
      sdpAnswer: null,
      denialReason: r['denialReason'],
    };
  }
}

function parseHeartbeatResponse(raw: unknown): HeartbeatResponse {
  if (typeof raw !== 'object' || raw === null) {
    fail('expected an object');
  }

  const r = raw as Record<string, unknown>;

  if (typeof r['renewed'] !== 'boolean') {
    fail("'renewed' must be a boolean");
  }

  if (!KNOWN_LIFECYCLE_STATES.has(r['lifecycleState'] as string)) {
    fail(`unknown lifecycleState '${String(r['lifecycleState'])}'`);
  }

  const remaining = r['remainingSeconds'];
  if (!isNullish(remaining)) {
    if (typeof remaining !== 'number' || !Number.isFinite(remaining) || remaining < 0) {
      fail("'remainingSeconds' must be a non-negative finite number or null");
    }
  }

  const forced = r['forcedCloseReason'];
  if (!isNullish(forced) && typeof forced !== 'string') {
    fail("'forcedCloseReason' must be a string or null");
  }

  return {
    renewed: r['renewed'],
    lifecycleState: r['lifecycleState'] as HeartbeatLifecycleState,
    remainingSeconds: isNullish(remaining) ? null : (remaining as number),
    forcedCloseReason: isNullish(forced) ? null : (forced as string),
  };
}

// ── CSRF guard ────────────────────────────────────────────────────────────────

function guardCsrfToken(token: string, operation: string): void {
  if (!token.trim()) {
    throw new Error(`${operation}: csrfToken must not be blank`);
  }
}

// ── API functions ─────────────────────────────────────────────────────────────

/**
 * Requests admission for a new voice session. Submits the client SDP offer and receives either an
 * admitted response (with session id and SDP answer) or a denial (with reason).
 */
export async function admitVoice(
  sdpOffer: string,
  csrfToken: string,
  signal?: AbortSignal,
): Promise<VoiceAdmissionResponse> {
  guardCsrfToken(csrfToken, 'admitVoice');

  const response = await fetch('/api/voice/admit', {
    method: 'POST',
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': csrfToken,
    },
    body: JSON.stringify({ sdpOffer }),
    signal,
  });

  if (!response.ok) {
    throw new Error(`admitVoice failed with status ${response.status}`);
  }

  return parseAdmissionResponse(await response.json());
}

/**
 * Renews a voice session's heartbeat. A 404 from the server is mapped to lifecycleState
 * 'not_found' rather than thrown, because not-found is an expected domain outcome.
 */
export async function heartbeatVoice(
  voiceSessionId: string,
  csrfToken: string,
  signal?: AbortSignal,
): Promise<HeartbeatResponse> {
  guardCsrfToken(csrfToken, 'heartbeatVoice');

  const response = await fetch('/api/voice/heartbeat', {
    method: 'POST',
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': csrfToken,
    },
    body: JSON.stringify({ voiceSessionId }),
    signal,
  });

  if (response.status === 404) {
    return { renewed: false, lifecycleState: 'not_found', remainingSeconds: null, forcedCloseReason: null };
  }

  if (!response.ok) {
    throw new Error(`heartbeatVoice failed with status ${response.status}`);
  }

  return parseHeartbeatResponse(await response.json());
}

/**
 * Releases a voice session explicitly. Success returns void; no JSON body is assumed.
 */
export async function releaseVoice(
  voiceSessionId: string,
  csrfToken: string,
  signal?: AbortSignal,
): Promise<void> {
  guardCsrfToken(csrfToken, 'releaseVoice');

  const response = await fetch('/api/voice/release', {
    method: 'POST',
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': csrfToken,
    },
    body: JSON.stringify({ voiceSessionId }),
    signal,
  });

  if (!response.ok) {
    throw new Error(`releaseVoice failed with status ${response.status}`);
  }
}
