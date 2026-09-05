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
 *
 * Every read tolerates `localStorage` itself misbehaving - a corrupt value, or `getItem` throwing -
 * by treating it exactly like "nothing stored", since that is always the safe default for a read.
 * Every write or remove instead returns a `boolean`: `true` means the operation completed, `false`
 * means `localStorage` itself threw (a `SecurityError` when storage is disabled or blocked, a
 * `QuotaExceededError` when it is full, or anything else a browser might raise) and nothing was
 * persisted. Nothing in this module ever lets such an exception escape - see the callers' own
 * comments for what each one does with a `false`.
 */

const KEY_PREFIX = 'mca.conversation.'

/** One browser profile's outstanding Turn for a single web conversation. `contentText` is `null`
 * exactly when the original message contained a confirmation token - see `redactedContentText`
 * below - so the resumption breadcrumb it carries is never a live single-use secret at rest. */
export interface InFlightTurn {
  nativeMessageId: string
  contentText: string | null
  turnId: string | null
}

function keyFor(webConversationId: string, participantId: string): string {
  return `${KEY_PREFIX}${webConversationId}.${participantId}`
}

/**
 * The exact shape of a `ConfirmationToken` (see `MultiChannelAgent.Domain.Inventories.
 * ConfirmationToken`): 32 cryptographically random bytes as unpadded base64url - exactly 43
 * characters of `[A-Za-z0-9_-]` - appearing as its own standalone run rather than as part of a
 * longer one. The lookbehind/lookahead are what "standalone" means here: neither the character
 * immediately before nor immediately after the 43-character run may itself be in the same class,
 * or this would just be an arbitrary slice of a longer (or shorter) run that is not actually a
 * token at all.
 *
 * This is deliberately a shape match, not a grammar match. The server's own command grammar accepts
 * free-form affirmatives - "confirm", "yes", "approve", "go ahead", and whatever else it recognizes
 * or comes to - with the token appearing anywhere among them, on one line or several. Matching that
 * grammar on the client would mean keeping two implementations of it in sync forever, and drifting
 * silently the moment they didn't. Matching the token's own shape instead needs no such parity: the
 * token is what is secret, so finding it anywhere in the content - regardless of which words, if
 * any, surround it - is what has to redact the whole thing.
 */
const CONFIRMATION_TOKEN = /(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])/

/** Redacts content to `null` before it is ever persisted, whenever a well-formed confirmation token
 * appears anywhere within it - see `CONFIRMATION_TOKEN`. This is deliberately conservative: it does
 * not attempt to recognize which words made the content a confirmation, only whether it contains
 * something shaped like the one thing in it that is ever secret. Content that merely comes close -
 * one character short, one character too many, or the right total length but not one contiguous run
 * - carries no such token and is returned unchanged. */
function redactedContentText(contentText: string): string | null {
  return CONFIRMATION_TOKEN.test(contentText) ? null : contentText
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

/** Runs a storage-mutating operation, treating any exception `localStorage` itself can throw as a
 * plain, recoverable failure rather than letting it escape into whatever the caller was doing.
 * Returns whether the operation completed. */
function tryStorageWrite(operation: () => void): boolean {
  try {
    operation()
    return true
  } catch {
    return false
  }
}

/** Reads the in-flight Turn for a conversation and Participant, or null if there is none -
 * including when `localStorage.getItem` itself throws, or the stored value is corrupt (not JSON) or
 * well-formed JSON of the wrong shape. Never throws. */
export function readInFlightTurn(webConversationId: string, participantId: string): InFlightTurn | null {
  let raw: string | null

  try {
    raw = localStorage.getItem(keyFor(webConversationId, participantId))
  } catch {
    return null
  }

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

/**
 * Records a just-submitted message as the conversation's in-flight Turn, overwriting whatever was
 * there before for this Participant. `turnId` is always null here: it is filled in later, by
 * `rememberTurnId`, once the HTTP response naming the Turn actually arrives. `contentText` is
 * redacted to `null` first if it contains a confirmation token - see `redactedContentText`.
 *
 * Returns whether the write completed. A caller about to send mutation-capable work under the
 * `nativeMessageId` this record exists to recover MUST check this before sending anything: a
 * `false` here means there would be nothing anywhere to resume or de-duplicate by if the response
 * were then lost, which is not a risk to take silently.
 */
export function rememberSubmission(
  webConversationId: string,
  participantId: string,
  submission: { nativeMessageId: string; contentText: string },
): boolean {
  const record: InFlightTurn = {
    nativeMessageId: submission.nativeMessageId,
    contentText: redactedContentText(submission.contentText),
    turnId: null,
  }
  return tryStorageWrite(() => localStorage.setItem(keyFor(webConversationId, participantId), JSON.stringify(record)))
}

/**
 * Fills in the Turn id on an existing in-flight record once the HTTP response names it. Requires
 * the `nativeMessageId` that response belongs to, and does nothing unless a valid existing record
 * is stored AND its `nativeMessageId` equals the one supplied here - it never silently creates a
 * record from just a turn id (missing the `nativeMessageId`/`contentText` a resumed UI needs), and
 * it never lets a response for a submission that has since been superseded overwrite whatever the
 * browser profile submitted next. Without that check, a stale response arriving after a second
 * submission - in this tab or another tab of the same browser profile - would stamp its turn id
 * onto the newer, unrelated record.
 *
 * Returns whether the write completed (or whether there was nothing to do, which is not a
 * failure). Safe to treat as best-effort: the accepted Turn id this call could not persist is not
 * lost, only unrecorded here - the server already has it, and a future resume that still finds
 * `turnId: null` simply resubmits the same `nativeMessageId`, which the boundary answers
 * idempotently rather than by doing the work twice.
 */
export function rememberTurnId(
  webConversationId: string,
  participantId: string,
  nativeMessageId: string,
  turnId: string,
): boolean {
  const existing = readInFlightTurn(webConversationId, participantId)

  if (existing === null || existing.nativeMessageId !== nativeMessageId) {
    return true
  }

  const record: InFlightTurn = { ...existing, turnId }
  return tryStorageWrite(() => localStorage.setItem(keyFor(webConversationId, participantId), JSON.stringify(record)))
}

/**
 * Unconditionally removes the in-flight Turn record for a conversation and Participant, e.g. once
 * starting a new conversation makes whatever was outstanding irrelevant regardless of which Turn it
 * named. Prefer `clearInFlightTurnIfMatches` wherever a specific Turn - rather than the whole
 * conversation - is what just concluded, since an unconditional clear here can discard a newer,
 * unrelated record that has since superseded it.
 *
 * Returns whether the removal completed. Safe to treat as best-effort wherever it is not the
 * Participant's own explicit action being confirmed: a record this call could not remove is merely
 * stale, not unsafe - the Turn it names is still answered exactly once, server-side.
 */
export function clearInFlightTurn(webConversationId: string, participantId: string): boolean {
  return tryStorageWrite(() => localStorage.removeItem(keyFor(webConversationId, participantId)))
}

/**
 * Removes the in-flight Turn record only if it still matches the given discriminator - either the
 * same `nativeMessageId` (the direct, synchronous "this submission was already answered" case) or
 * the same `turnId` (the streamed terminal-Outcome case). A Turn's own belated completion names
 * only itself; without this check, it could otherwise clear a newer Turn's record out from under it
 * - one a Participant had already submitted by the time the belated one concluded.
 *
 * Returns whether the operation completed - `true` whenever nothing needed removing (no record, or
 * one that does not match), and otherwise whatever the underlying `clearInFlightTurn` returns. Safe
 * to treat as best-effort, for the same reason `clearInFlightTurn` is.
 */
export function clearInFlightTurnIfMatches(
  webConversationId: string,
  participantId: string,
  discriminator: { nativeMessageId: string } | { turnId: string },
): boolean {
  const existing = readInFlightTurn(webConversationId, participantId)

  if (existing === null) {
    return true
  }

  const matches =
    'nativeMessageId' in discriminator
      ? existing.nativeMessageId === discriminator.nativeMessageId
      : existing.turnId === discriminator.turnId

  return matches ? clearInFlightTurn(webConversationId, participantId) : true
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
