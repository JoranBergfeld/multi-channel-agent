import { describe, expect, it, vi } from 'vitest'

import {
  clearInFlightTurn,
  clearInFlightTurnIfMatches,
  readInFlightTurn,
  rememberSubmission,
  rememberTurnId,
  subscribeToConversationChanges,
} from './conversationStorage'

const CONVERSATION = 'web-conversation-1'
const OTHER_CONVERSATION = 'web-conversation-2'
const PARTICIPANT = 'participant-1'
const OTHER_PARTICIPANT = 'participant-2'

/** The literal storage key layout, kept independent of the module's own (private) prefix constant
 * so a test that pokes raw `localStorage` proves the real on-disk shape rather than whatever the
 * module happens to compute internally. */
function rawKeyFor(webConversationId: string, participantId: string): string {
  return `mca.conversation.${webConversationId}.${participantId}`
}

describe('readInFlightTurn', () => {
  it('returns null when no record has ever been stored for the conversation', () => {
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull()
  })

  it('returns null and does not throw for a corrupt, non-JSON value', () => {
    localStorage.setItem(rawKeyFor(CONVERSATION, PARTICIPANT), 'not-json{')

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull()
  })

  it('returns null for well-formed JSON that is the wrong shape', () => {
    localStorage.setItem(rawKeyFor(CONVERSATION, PARTICIPANT), JSON.stringify({ turnId: 42 }))

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull()
  })

  it('returns null for well-formed JSON with all three valid fields plus an unexpected extra field', () => {
    localStorage.setItem(
      rawKeyFor(CONVERSATION, PARTICIPANT),
      JSON.stringify({
        nativeMessageId: 'native-1',
        contentText: 'list stock',
        turnId: 'turn-1',
        secret: 'confirmation-token-should-never-be-here',
      }),
    )

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull()
  })

  it('keeps records for different conversations isolated', () => {
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' })

    expect(readInFlightTurn(OTHER_CONVERSATION, PARTICIPANT)).toBeNull()
  })

  it('keeps records for two Participants sharing the same web conversation id isolated', () => {
    // The same browser profile, signed out and back in as someone else, still carries the same
    // long-lived `WebConversationCookie` - so the conversation id alone is not enough to tell the
    // two Participants' records apart. Only the Participant scope does.
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' })

    expect(readInFlightTurn(CONVERSATION, OTHER_PARTICIPANT)).toBeNull()
  })

  it('never lets a newly signed-in Participant resume or resubmit a prior Participant\'s message on the same browser profile', () => {
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'confidential request' })
    rememberTurnId(CONVERSATION, PARTICIPANT, 'native-1', 'turn-1')

    // A new Participant signs in on the same browser profile, before the prior Participant's
    // in-flight Turn ever resolved. Their own read of this conversation must see nothing at all -
    // not the prior Participant's content, and not their unresolved Turn id.
    expect(readInFlightTurn(CONVERSATION, OTHER_PARTICIPANT)).toBeNull()

    // And the prior Participant's own record is untouched by the new Participant ever having looked.
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual({
      nativeMessageId: 'native-1',
      contentText: 'confidential request',
      turnId: 'turn-1',
    })
  })

  it('accepts a stored record whose contentText is null, exactly as a redacted confirmation writes it', () => {
    localStorage.setItem(
      rawKeyFor(CONVERSATION, PARTICIPANT),
      JSON.stringify({ nativeMessageId: 'native-1', contentText: null, turnId: null }),
    )

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual({
      nativeMessageId: 'native-1',
      contentText: null,
      turnId: null,
    })
  })
})

describe('rememberSubmission', () => {
  it('stores the native message id and content text with turnId null before any HTTP response arrives', () => {
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' })

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual({
      nativeMessageId: 'native-1',
      contentText: 'list stock',
      turnId: null,
    })
  })

  it('serializes exactly contentText, nativeMessageId, and turnId - never a Participant id, answer, payload, or token', () => {
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' })

    const raw = localStorage.getItem(rawKeyFor(CONVERSATION, PARTICIPANT))
    expect(raw).not.toBeNull()
    const parsed = JSON.parse(raw!) as Record<string, unknown>
    expect(Object.keys(parsed).sort()).toEqual(['contentText', 'nativeMessageId', 'turnId'])
  })
})

describe('rememberSubmission redacting a confirmation command', () => {
  it('redacts a confirm command\'s content to null, keeping the token out of raw localStorage and the record\'s three approved keys intact', () => {
    rememberSubmission(CONVERSATION, PARTICIPANT, {
      nativeMessageId: 'native-1',
      contentText: 'confirm FAKE-TOKEN-DO-NOT-LOG',
    })

    const raw = localStorage.getItem(rawKeyFor(CONVERSATION, PARTICIPANT))
    expect(raw).not.toBeNull()
    expect(raw).not.toContain('FAKE-TOKEN-DO-NOT-LOG')
    expect(raw).not.toContain('confirm')

    const parsed = JSON.parse(raw!) as Record<string, unknown>
    expect(Object.keys(parsed).sort()).toEqual(['contentText', 'nativeMessageId', 'turnId'])
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual({
      nativeMessageId: 'native-1',
      contentText: null,
      turnId: null,
    })
  })

  it('recognizes the confirm command leniently by case and whitespace, and only that shape', () => {
    rememberSubmission(CONVERSATION, PARTICIPANT, {
      nativeMessageId: 'native-1',
      contentText: '  CONFIRM   FAKE-TOKEN-DO-NOT-LOG  ',
    })
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)?.contentText).toBeNull()

    rememberSubmission(CONVERSATION, PARTICIPANT, {
      nativeMessageId: 'native-2',
      contentText: 'Confirm fake-token-do-not-log',
    })
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)?.contentText).toBeNull()

    // Neither carries a token - a bare "confirm" with nothing after it, and a sentence that merely
    // mentions the word - so neither is secret, and both are stored exactly as typed.
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-3', contentText: 'confirm' })
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)?.contentText).toBe('confirm')

    rememberSubmission(CONVERSATION, PARTICIPANT, {
      nativeMessageId: 'native-4',
      contentText: 'please confirm this order',
    })
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)?.contentText).toBe('please confirm this order')

    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-5', contentText: 'reject' })
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)?.contentText).toBe('reject')

    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-6', contentText: 'list stock' })
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)?.contentText).toBe('list stock')
  })
})

describe('rememberTurnId', () => {
  it('updates the existing record with the turn id once the HTTP response arrives', () => {
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' })

    rememberTurnId(CONVERSATION, PARTICIPANT, 'native-1', 'turn-1')

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual({
      nativeMessageId: 'native-1',
      contentText: 'list stock',
      turnId: 'turn-1',
    })
  })

  it('keeps the newer submission untouched when a turn id arrives for an older, already-superseded submission', () => {
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-A', contentText: 'list stock' })
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-B', contentText: 'find bolts' })

    rememberTurnId(CONVERSATION, PARTICIPANT, 'native-A', 'turn-A')

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual({
      nativeMessageId: 'native-B',
      contentText: 'find bolts',
      turnId: null,
    })
  })

  it('does not silently create a record when there is no existing submission', () => {
    rememberTurnId(CONVERSATION, PARTICIPANT, 'native-1', 'turn-1')

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull()
  })

  it('never stamps a turn id onto another Participant\'s record for the same web conversation id', () => {
    rememberSubmission(CONVERSATION, OTHER_PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' })

    rememberTurnId(CONVERSATION, PARTICIPANT, 'native-1', 'turn-1')

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull()
    expect(readInFlightTurn(CONVERSATION, OTHER_PARTICIPANT)).toEqual({
      nativeMessageId: 'native-1',
      contentText: 'list stock',
      turnId: null,
    })
  })
})

describe('clearInFlightTurn', () => {
  it('removes the record for the conversation', () => {
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' })
    rememberTurnId(CONVERSATION, PARTICIPANT, 'native-1', 'turn-1')

    clearInFlightTurn(CONVERSATION, PARTICIPANT)

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull()
  })

  it('never removes another Participant\'s record for the same web conversation id', () => {
    rememberSubmission(CONVERSATION, OTHER_PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' })

    clearInFlightTurn(CONVERSATION, PARTICIPANT)

    expect(readInFlightTurn(CONVERSATION, OTHER_PARTICIPANT)).toEqual({
      nativeMessageId: 'native-1',
      contentText: 'list stock',
      turnId: null,
    })
  })
})

describe('clearInFlightTurnIfMatches', () => {
  it('removes the record when the turnId discriminator matches the stored turnId', () => {
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' })
    rememberTurnId(CONVERSATION, PARTICIPANT, 'native-1', 'turn-1')

    clearInFlightTurnIfMatches(CONVERSATION, PARTICIPANT, { turnId: 'turn-1' })

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull()
  })

  it('leaves the record untouched when the turnId discriminator does not match a newer, superseding record', () => {
    // Turn A's own stream reports its terminal Outcome after a Turn B has already been submitted,
    // overwriting the record. A's belated completion names only itself and must not erase B's.
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-A', contentText: 'list stock' })
    rememberTurnId(CONVERSATION, PARTICIPANT, 'native-A', 'turn-A')
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-B', contentText: 'find bolts' })

    clearInFlightTurnIfMatches(CONVERSATION, PARTICIPANT, { turnId: 'turn-A' })

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual({
      nativeMessageId: 'native-B',
      contentText: 'find bolts',
      turnId: null,
    })
  })

  it('removes the record when the nativeMessageId discriminator matches the stored nativeMessageId', () => {
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' })

    clearInFlightTurnIfMatches(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1' })

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull()
  })

  it('leaves the record untouched when the nativeMessageId discriminator does not match a newer, superseding record', () => {
    // The direct, synchronous "already answered" reply to a submission whose response was slow to
    // arrive - by the time it does, a newer submission has already overwritten the record.
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-A', contentText: 'list stock' })
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-B', contentText: 'find bolts' })

    clearInFlightTurnIfMatches(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-A' })

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual({
      nativeMessageId: 'native-B',
      contentText: 'find bolts',
      turnId: null,
    })
  })

  it('does nothing when there is no record at all', () => {
    expect(() => clearInFlightTurnIfMatches(CONVERSATION, PARTICIPANT, { turnId: 'turn-1' })).not.toThrow()

    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull()
  })
})

describe('subscribeToConversationChanges', () => {
  it('invokes the callback for a storage event carrying exactly this conversation and Participant scope', () => {
    const onChanged = vi.fn()
    subscribeToConversationChanges(CONVERSATION, PARTICIPANT, onChanged)

    window.dispatchEvent(new StorageEvent('storage', { key: rawKeyFor(CONVERSATION, PARTICIPANT) }))

    expect(onChanged).toHaveBeenCalledTimes(1)
  })

  it('ignores storage events for another conversation and for unrelated keys', () => {
    const onChanged = vi.fn()
    subscribeToConversationChanges(CONVERSATION, PARTICIPANT, onChanged)

    window.dispatchEvent(new StorageEvent('storage', { key: rawKeyFor(OTHER_CONVERSATION, PARTICIPANT) }))
    window.dispatchEvent(new StorageEvent('storage', { key: 'unrelated.key' }))

    expect(onChanged).not.toHaveBeenCalled()
  })

  it('ignores a storage event for the same web conversation id but a different Participant', () => {
    const onChanged = vi.fn()
    subscribeToConversationChanges(CONVERSATION, PARTICIPANT, onChanged)

    window.dispatchEvent(new StorageEvent('storage', { key: rawKeyFor(CONVERSATION, OTHER_PARTICIPANT) }))

    expect(onChanged).not.toHaveBeenCalled()
  })

  it('stops delivering notifications once unsubscribed', () => {
    const onChanged = vi.fn()
    const unsubscribe = subscribeToConversationChanges(CONVERSATION, PARTICIPANT, onChanged)

    unsubscribe()
    window.dispatchEvent(new StorageEvent('storage', { key: rawKeyFor(CONVERSATION, PARTICIPANT) }))

    expect(onChanged).not.toHaveBeenCalled()
  })
})
