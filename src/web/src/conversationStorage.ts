/**
 * Persists one browser profile's single in-flight Turn for one web conversation, so a page
 * refresh, a crashed tab, or a second tab open on the same browser profile can find - and resume
 * watching - whatever Turn is still outstanding. Scoped under `localStorage` deliberately: that
 * scope matches the lifetime and reach of the long-lived, HttpOnly `WebConversationCookie` this
 * record is keyed against, so the two always agree on which browser profile they describe.
 *
 * Every record is scoped by BOTH `webConversationId` and `participantId`. The conversation cookie
 * alone is not enough: it is as long-lived as the browser profile itself, so it survives a sign-out
 * and a different Participant signing back in on the very same browser - without the Participant
 * scope, that new Participant could resume, or even resubmit, a message the prior Participant never
 * saw answered. `participantId` lives only in the storage *key*, never in the record's own fields,
 * so it can never end up serialized as if it were part of the resumption breadcrumb itself.
 *
 * This is a resumption breadcrumb, not a cache of the conversation itself - it never stores an
 * answer, a payload, an auth token, or any other secret or confirmation-bearing value. Only the
 * three fields below are ever written or read back.
 */

const KEY_PREFIX = 'mca.conversation.'

/** One browser profile's outstanding Turn for a single web conversation. `contentText` is `null`
 * exactly when the original message was a confirmation command - see `redactedContentText` below -
 * so the resumption breadcrumb it carries is never a live single-use secret at rest. */
export interface InFlightTurn {
  nativeMessageId: string
  contentText: string | null
  turnId: string | null
}

function keyFor(webConversationId: string, participantId: string): string {
  return `${KEY_PREFIX}${webConversationId}.${participantId}`
}

/** This application's confirmation command shape - `confirm <token>` - tolerant of the same
 * surrounding whitespace and case the server's own command grammar is, since a Participant can type
 * it by hand exactly as readily as the UI's own "Confirm" button constructs it. */
const CONFIRMATION_COMMAND = /^\s*confirm\s+\S+\s*$/i

/** Redacts a confirmation command's content to `null` before it is ever persisted - its token is a
 * single-use secret the Participant must be able to quote back, not something this browser profile
 * should still hold once it has been typed. Anything else - including a bare "confirm" with no
 * token, or a sentence that merely mentions the word - carries no secret and is returned unchanged. */
function redactedContentText(contentText: string): string | null {
  return CONFIRMATION_COMMAND.test(contentText) ? null : contentText
}

/** Runtime type guard: rejects anything that is not exactly the three-field shape above - no fewer
 * and no more - so a corrupted or foreign value read back from storage is treated as absent rather
 * than trusted. Rejecting rather than normalizing away extra keys matters here: it guarantees an
 * unknown or secret field can never be re-persisted by a later write that spreads the record read
 * back in (e.g. `rememberTurnId`), because that write never sees the record at all. */
function isInFlightTurn(value: unknown): value is InFlightTurn {
  if (typeof value !== 'object' || value === null) {
    return false
  }

  const record = value as Record<string, unknown>

  return (
    Object.keys(record).length === 3 &&
    typeof record.nativeMessageId === 'string' &&
    (record.contentText === null || typeof record.contentText === 'string') &&
    (record.turnId === null || typeof record.turnId === 'string')
  )
}

/** Reads the in-flight Turn for a conversation and Participant, or null if there is none -
 * including when the stored value is corrupt (not JSON) or well-formed JSON of the wrong shape.
 * Never throws. */
export function readInFlightTurn(webConversationId: string, participantId: string): InFlightTurn | null {
  const raw = localStorage.getItem(keyFor(webConversationId, participantId))

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
 * was there before for this Participant. `turnId` is always null here: it is filled in later, by
 * `rememberTurnId`, once the HTTP response naming the Turn actually arrives. `contentText` is
 * redacted to `null` first if the message was a confirmation command - see `redactedContentText`. */
export function rememberSubmission(
  webConversationId: string,
  participantId: string,
  submission: { nativeMessageId: string; contentText: string },
): void {
  const record: InFlightTurn = {
    nativeMessageId: submission.nativeMessageId,
    contentText: redactedContentText(submission.contentText),
    turnId: null,
  }
  localStorage.setItem(keyFor(webConversationId, participantId), JSON.stringify(record))
}

/** Fills in the Turn id on an existing in-flight record once the HTTP response names it. Requires
 * the `nativeMessageId` that response belongs to, and does nothing unless a valid existing record
 * is stored AND its `nativeMessageId` equals the one supplied here - it never silently creates a
 * record from just a turn id (missing the `nativeMessageId`/`contentText` a resumed UI needs), and
 * it never lets a response for a submission that has since been superseded overwrite whatever the
 * browser profile submitted next. Without that check, a stale response arriving after a second
 * submission - in this tab or another tab of the same browser profile - would stamp its turn id
 * onto the newer, unrelated record. */
export function rememberTurnId(
  webConversationId: string,
  participantId: string,
  nativeMessageId: string,
  turnId: string,
): void {
  const existing = readInFlightTurn(webConversationId, participantId)

  if (existing === null || existing.nativeMessageId !== nativeMessageId) {
    return
  }

  const record: InFlightTurn = { ...existing, turnId }
  localStorage.setItem(keyFor(webConversationId, participantId), JSON.stringify(record))
}

/** Unconditionally removes the in-flight Turn record for a conversation and Participant, e.g. once
 * starting a new conversation makes whatever was outstanding irrelevant regardless of which Turn it
 * named. Prefer `clearInFlightTurnIfMatches` wherever a specific Turn - rather than the whole
 * conversation - is what just concluded, since an unconditional clear here can discard a newer,
 * unrelated record that has since superseded it. */
export function clearInFlightTurn(webConversationId: string, participantId: string): void {
  localStorage.removeItem(keyFor(webConversationId, participantId))
}

/**
 * Removes the in-flight Turn record only if it still matches the given discriminator - either the
 * same `nativeMessageId` (the direct, synchronous "this submission was already answered" case) or
 * the same `turnId` (the streamed terminal-Outcome case). A Turn's own belated completion names
 * only itself; without this check, it could otherwise clear a newer Turn's record out from under it
 * - one a Participant had already submitted by the time the belated one concluded.
 */
export function clearInFlightTurnIfMatches(
  webConversationId: string,
  participantId: string,
  discriminator: { nativeMessageId: string } | { turnId: string },
): void {
  const existing = readInFlightTurn(webConversationId, participantId)

  if (existing === null) {
    return
  }

  const matches =
    'nativeMessageId' in discriminator
      ? existing.nativeMessageId === discriminator.nativeMessageId
      : existing.turnId === discriminator.turnId

  if (matches) {
    clearInFlightTurn(webConversationId, participantId)
  }
}

/** Notifies `onChanged` whenever another tab on the same browser profile changes this exact
 * conversation-and-Participant scope's in-flight Turn record (the browser's `storage` event only
 * ever fires in other tabs, never the one that made the change). Returns an unsubscribe function. */
export function subscribeToConversationChanges(
  webConversationId: string,
  participantId: string,
  onChanged: () => void,
): () => void {
  const key = keyFor(webConversationId, participantId)

  const listener = (event: StorageEvent) => {
    if (event.key === key) {
      onChanged()
    }
  }

  window.addEventListener('storage', listener)

  return () => window.removeEventListener('storage', listener)
}
