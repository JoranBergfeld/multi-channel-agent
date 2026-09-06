import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { recordingEventStreamFactory } from './testing/fakeEventSource';
import { readInFlightTurn } from './conversationStorage';
import { useTurnSubmission, type UseTurnSubmissionOptions } from './useTurnSubmission';

const CONVERSATION = 'web-conversation-1';
const PARTICIPANT = 'participant-1';

function stubFetch(responder: (url: string, init?: RequestInit) => Response) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) =>
    Promise.resolve(responder(String(input), init)),
  );
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function acceptedResponse(turnId: string) {
  return new Response(JSON.stringify({ turnId, alreadyAccepted: false }), {
    status: 202,
    headers: { 'Content-Type': 'application/json' },
  });
}

function hookWith(overrides?: Partial<UseTurnSubmissionOptions>) {
  const { opened, factory } = recordingEventStreamFactory();
  const options: UseTurnSubmissionOptions = {
    csrfToken: 'csrf-token',
    webConversationId: CONVERSATION,
    participantId: PARTICIPANT,
    onTerminalOutcome: vi.fn(),
    createSource: factory,
    ...overrides,
  };
  return { ...renderHook(() => useTurnSubmission(options)), opened, options };
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('useTurnSubmission', () => {
  it('stores breadcrumb in localStorage BEFORE the HTTP request', async () => {
    const setItemSpy = vi.spyOn(Storage.prototype, 'setItem');
    const fetchMock = vi.fn(() => new Promise<Response>(() => {}));
    vi.stubGlobal('fetch', fetchMock);

    const { result } = hookWith();

    await act(async () => {
      result.current.submit({ nativeMessageId: 'msg-1', contentText: 'add five' });
    });

    expect(setItemSpy).toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalled();
    expect(setItemSpy.mock.invocationCallOrder[0]).toBeLessThan(fetchMock.mock.invocationCallOrder[0]);
    setItemSpy.mockRestore();
  });

  it('returns false synchronously when a submission is already in flight', async () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})));

    const { result } = hookWith();

    let first!: boolean;
    await act(async () => {
      first = result.current.submit({ nativeMessageId: 'msg-a', contentText: 'add five' });
    });
    expect(first).toBe(true);
    expect(result.current.progress).toBe('submitting');

    let second!: boolean;
    act(() => {
      second = result.current.submit({ nativeMessageId: 'msg-b', contentText: 'list stock' });
    });
    expect(second).toBe(false);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it('includes voiceSessionId in the request body when provided', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-1'));
    const { result } = hookWith();

    await act(async () => {
      result.current.submit({
        nativeMessageId: 'voice:vs-1:item_1',
        contentText: 'add five',
        voiceSessionId: 'vs-1',
      });
    });

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body));
    expect(body.voiceSessionId).toBe('vs-1');
    expect(body.nativeMessageId).toBe('voice:vs-1:item_1');
    expect(body.contentText).toBe('add five');
  });

  it('omits voiceSessionId from the request body for text submissions', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-1'));
    const { result } = hookWith();

    await act(async () => {
      result.current.submit({ nativeMessageId: 'msg-1', contentText: 'list stock' });
    });

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body));
    expect(body).not.toHaveProperty('voiceSessionId');
  });

  it('transitions to accepted and exposes the turnId after a 202 response', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { result } = hookWith();

    await act(async () => {
      result.current.submit({ nativeMessageId: 'msg-1', contentText: 'list stock' });
    });

    await waitFor(() => {
      expect(result.current.turnId).toBe('turn-1');
      expect(result.current.progress).toBe('accepted');
    });
  });

  it('closes the stream and ignores late callbacks after unmount', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const onTerminalOutcome = vi.fn();
    const { result, unmount, opened } = hookWith({ onTerminalOutcome });

    await act(async () => {
      result.current.submit({ nativeMessageId: 'msg-1', contentText: 'list stock' });
    });

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(opened[0].closed).toBe(false);

    unmount();
    expect(opened[0].closed).toBe(true);

    opened[0].emit(
      'outcome',
      { turnId: 'turn-1', status: 'completed', category: 'completed', code: 'echo', summary: 'Hi.', deliveries: [] },
      '1000000',
    );

    expect(onTerminalOutcome).not.toHaveBeenCalled();
  });

  it('clears the matching breadcrumb when a terminal Outcome arrives via stream', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { result, opened } = hookWith();

    await act(async () => {
      result.current.submit({ nativeMessageId: 'msg-1', contentText: 'list stock' });
    });

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).not.toBeNull();

    act(() => {
      opened[0].emit(
        'outcome',
        { turnId: 'turn-1', status: 'completed', category: 'completed', code: 'echo', summary: 'Hi.', deliveries: [] },
        '1000000',
      );
    });

    await waitFor(() => expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull());
  });

  it('does not send the HTTP request when browser storage is unavailable, and stays usable', () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-should-not-happen'));
    const spy = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Storage is disabled', 'SecurityError');
    });

    try {
      const { result } = hookWith();

      let accepted!: boolean;
      act(() => {
        accepted = result.current.submit({ nativeMessageId: 'msg-1', contentText: 'list stock' });
      });

      expect(accepted).toBe(false);
      expect(fetchMock).not.toHaveBeenCalled();
      expect(result.current.error).toMatch(/storage/i);
      expect(result.current.progress).toBe('idle');
    } finally {
      spy.mockRestore();
    }
  });
});
