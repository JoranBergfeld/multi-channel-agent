/**
 * Persists one browser profile's single in-flight Turn for one web conversation, so a page
 * refresh, a crashed tab, or a second tab open on the same browser profile can find - and resume
 * watching - whatever Turn is still outstanding. Scoped under `localStorage` deliberately: that
 * scope matches the lifetime and reach of the long-lived, HttpOnly `WebConversationCookie` this
 * record is keyed against, so the two always agree on which browser profile they describe.
 *
 * This is a resumption breadcrumb, not a cache of the conversation itself - it never stores an
 * answer, a payload, an auth token, or any other secret or confirmation-bearing value. Only the
 * three fields below are ever written or read back.
 */

const KEY_PREFIX = 'mca.conversation.'

/** One browser profile's outstanding Turn for a single web conversation. */
export interface InFlightTurn {
  nativeMessageId: string
  contentText: string
  turnId: string | null
}

function keyFor(webConversationId: string): string {
  return `${KEY_PREFIX}${webConversationId}`
}

/** Runtime type guard: rejects anything that is not exactly the three-field shape above, so a
 * corrupted or foreign value read back from storage is treated as absent rather than trusted. */
function isInFlightTurn(value: unknown): value is InFlightTurn {
  if (typeof value !== 'object' || value === null) {
    return false
  }

  const record = value as Record<string, unknown>

  return (
    typeof record.nativeMessageId === 'string' &&
    typeof record.contentText === 'string' &&
    (record.turnId === null || typeof record.turnId === 'string')
  )
}

/** Reads the in-flight Turn for a conversation, or null if there is none - including when the
 * stored value is corrupt (not JSON) or well-formed JSON of the wrong shape. Never throws. */
export function readInFlightTurn(webConversationId: string): InFlightTurn | null {
  const raw = localStorage.getItem(keyFor(webConversationId))

  if (raw === null) {
    return null
  }

  let parsed: unknown

  try {
    parsed = JSON.parse(raw)
  } catch {
    return null
  }

  return isInFlightTurn(parsed) ? parsed : null
}

/** Records a just-submitted message as the conversation's in-flight Turn, overwriting whatever
 * was there before. `turnId` is always null here: it is filled in later, by `rememberTurnId`, once
 * the HTTP response naming the Turn actually arrives. */
export function rememberSubmission(
  webConversationId: string,
  submission: { nativeMessageId: string; contentText: string },
): void {
  const record: InFlightTurn = { ...submission, turnId: null }
  localStorage.setItem(keyFor(webConversationId), JSON.stringify(record))
}

/** Fills in the Turn id on an existing in-flight record once the HTTP response names it. Does
 * nothing if there is no valid existing record - it never silently creates one from just a turn
 * id, which would be missing the `nativeMessageId`/`contentText` a resumed UI needs. */
export function rememberTurnId(webConversationId: string, turnId: string): void {
  const existing = readInFlightTurn(webConversationId)

  if (existing === null) {
    return
  }

  const record: InFlightTurn = { ...existing, turnId }
  localStorage.setItem(keyFor(webConversationId), JSON.stringify(record))
}

/** Removes the in-flight Turn record for a conversation, e.g. once its outcome has been
 * delivered and there is nothing left to resume. */
export function clearInFlightTurn(webConversationId: string): void {
  localStorage.removeItem(keyFor(webConversationId))
}

/** Notifies `onChanged` whenever another tab on the same browser profile changes this exact
 * conversation's in-flight Turn record (the browser's `storage` event only ever fires in other
 * tabs, never the one that made the change). Returns an unsubscribe function. */
export function subscribeToConversationChanges(webConversationId: string, onChanged: () => void): () => void {
  const key = keyFor(webConversationId)

  const listener = (event: StorageEvent) => {
    if (event.key === key) {
      onChanged()
    }
  }

  window.addEventListener('storage', listener)

  return () => window.removeEventListener('storage', listener)
}
