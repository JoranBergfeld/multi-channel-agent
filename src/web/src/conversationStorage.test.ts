import { describe, expect, it, vi } from 'vitest'

import {
  clearInFlightTurn,
  readInFlightTurn,
  rememberSubmission,
  rememberTurnId,
  subscribeToConversationChanges,
} from './conversationStorage'

const CONVERSATION = 'web-conversation-1'
const OTHER_CONVERSATION = 'web-conversation-2'

/** The literal storage key layout, kept independent of the module's own (private) prefix constant
 * so a test that pokes raw `localStorage` proves the real on-disk shape rather than whatever the
 * module happens to compute internally. */
function rawKeyFor(webConversationId: string): string {
  return `mca.conversation.${webConversationId}`
}

describe('readInFlightTurn', () => {
  it('returns null when no record has ever been stored for the conversation', () => {
    expect(readInFlightTurn(CONVERSATION)).toBeNull()
  })

  it('returns null and does not throw for a corrupt, non-JSON value', () => {
    localStorage.setItem(rawKeyFor(CONVERSATION), 'not-json{')

    expect(readInFlightTurn(CONVERSATION)).toBeNull()
  })

  it('returns null for well-formed JSON that is the wrong shape', () => {
    localStorage.setItem(rawKeyFor(CONVERSATION), JSON.stringify({ turnId: 42 }))

    expect(readInFlightTurn(CONVERSATION)).toBeNull()
  })

  it('keeps records for different conversations isolated', () => {
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' })

    expect(readInFlightTurn(OTHER_CONVERSATION)).toBeNull()
  })
})

describe('rememberSubmission', () => {
  it('stores the native message id and content text with turnId null before any HTTP response arrives', () => {
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' })

    expect(readInFlightTurn(CONVERSATION)).toEqual({
      nativeMessageId: 'native-1',
      contentText: 'list stock',
      turnId: null,
    })
  })

  it('serializes exactly contentText, nativeMessageId, and turnId - never an answer, payload, or token', () => {
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' })

    const raw = localStorage.getItem(rawKeyFor(CONVERSATION))
    expect(raw).not.toBeNull()
    const parsed = JSON.parse(raw!) as Record<string, unknown>
    expect(Object.keys(parsed).sort()).toEqual(['contentText', 'nativeMessageId', 'turnId'])
  })
})

describe('rememberTurnId', () => {
  it('updates the existing record with the turn id once the HTTP response arrives', () => {
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' })

    rememberTurnId(CONVERSATION, 'turn-1')

    expect(readInFlightTurn(CONVERSATION)).toEqual({
      nativeMessageId: 'native-1',
      contentText: 'list stock',
      turnId: 'turn-1',
    })
  })

  it('does not silently create a record when there is no existing submission', () => {
    rememberTurnId(CONVERSATION, 'turn-1')

    expect(readInFlightTurn(CONVERSATION)).toBeNull()
  })
})

describe('clearInFlightTurn', () => {
  it('removes the record for the conversation', () => {
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' })
    rememberTurnId(CONVERSATION, 'turn-1')

    clearInFlightTurn(CONVERSATION)

    expect(readInFlightTurn(CONVERSATION)).toBeNull()
  })
})

describe('subscribeToConversationChanges', () => {
  it('invokes the callback for a storage event carrying exactly this conversation key', () => {
    const onChanged = vi.fn()
    subscribeToConversationChanges(CONVERSATION, onChanged)

    window.dispatchEvent(new StorageEvent('storage', { key: rawKeyFor(CONVERSATION) }))

    expect(onChanged).toHaveBeenCalledTimes(1)
  })

  it('ignores storage events for another conversation and for unrelated keys', () => {
    const onChanged = vi.fn()
    subscribeToConversationChanges(CONVERSATION, onChanged)

    window.dispatchEvent(new StorageEvent('storage', { key: rawKeyFor(OTHER_CONVERSATION) }))
    window.dispatchEvent(new StorageEvent('storage', { key: 'unrelated.key' }))

    expect(onChanged).not.toHaveBeenCalled()
  })

  it('stops delivering notifications once unsubscribed', () => {
    const onChanged = vi.fn()
    const unsubscribe = subscribeToConversationChanges(CONVERSATION, onChanged)

    unsubscribe()
    window.dispatchEvent(new StorageEvent('storage', { key: rawKeyFor(CONVERSATION) }))

    expect(onChanged).not.toHaveBeenCalled()
  })
})
