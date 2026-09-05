import { StrictMode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { recordingEventStreamFactory } from './testing/fakeEventSource';
import { rememberSubmission, rememberTurnId, readInFlightTurn } from './conversationStorage';
import TurnTracer from './TurnTracer';

const CONVERSATION = 'web-conversation-1';
const PARTICIPANT = 'participant-1';
/** Exactly the shape of a real `ConfirmationToken` (32 bytes as unpadded base64url, 43 characters
 * of `[A-Za-z0-9_-]`) - obviously fake, but the right length and character set to exercise
 * redaction. */
const FAKE_TOKEN = 'FAKE-TOKEN-DO-NOT-LOG0000000000000000000000';

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

function alreadyAnsweredResponse(turnId: string) {
  return new Response(
    JSON.stringify({
      turnId,
      status: 'completed',
      category: 'completed',
      code: 'stock.listed',
      summary: 'The recorded answer.',
      payload: null,
      deliveries: [],
    }),
    {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    },
  );
}

/** Flushes the microtask chain a pending `fetch`/`response.json()`/component continuation runs
 * through once its response resolves, without depending on any observable side effect - which is
 * exactly what a post-unmount continuation must not have. A macrotask tick is enough: every
 * microtask already queued (however many `await`s deep) always drains before it fires. */
async function flushAsyncWork() {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

function renderTracer(createSource: ReturnType<typeof recordingEventStreamFactory>['factory']) {
  return render(
    <TurnTracer
      csrfToken="csrf-token"
      webConversationId={CONVERSATION}
      participantId={PARTICIPANT}
      onTerminalOutcome={() => {}}
      createSource={createSource}
    />,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TurnTracer', () => {
  it('submits a Turn and follows its stream instead of polling', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(opened[0].url).toBe('/api/turns/turn-1/events');

    // Exactly one request: the submission. Nothing polls.
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('announces progress in a live region while the answer is still being worked on', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));

    opened[0].emit('accepted', { turnId: 'turn-1', receivedAt: '2026-09-04T10:00:00+00:00' }, '1');
    expect(await screen.findByRole('status')).toHaveTextContent('Accepted');

    opened[0].emit('processing', { turnId: 'turn-1', startedAt: '2026-09-04T10:00:01+00:00' }, '2');
    expect(await screen.findByRole('status')).toHaveTextContent('Working on it');
  });

  it('shows the fatal error and stops announcing progress when the open stream fails permanently', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));

    opened[0].emit('processing', { turnId: 'turn-1', startedAt: '2026-09-04T10:00:01+00:00' }, '2');
    expect(await screen.findByRole('status')).toHaveTextContent('Working on it');

    opened[0].failFatally();

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Lost the connection to this Turn and cannot resume it automatically. Refresh to try again.',
    );
    expect(screen.getByRole('status')).toHaveTextContent('');
  });

  it('renders the streamed parts and the terminal Outcome together', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));

    opened[0].emit(
      'part',
      { turnId: 'turn-1', order: 1, kind: 'text', text: 'One Stock Entry.', payload: null },
      '100',
    );
    opened[0].emit(
      'part',
      {
        turnId: 'turn-1',
        order: 2,
        kind: 'data',
        text: null,
        payload: {
          version: 1,
          kind: 'stock_list',
          rows: [{ id: 'entry-1', name: 'Steel Bolts', unit: 'each', location: null, note: null, quantity: '4' }],
          nextCursor: null,
          hasMore: false,
        },
      },
      '101',
    );
    opened[0].emit(
      'outcome',
      {
        turnId: 'turn-1',
        status: 'completed',
        category: 'completed',
        code: 'stock.listed',
        summary: 'One Stock Entry.',
        deliveries: [],
      },
      '1000000',
    );

    expect(await screen.findByText('stock.listed')).toBeInTheDocument();
    expect(screen.getByText('Steel Bolts')).toBeInTheDocument();
  });

  it('reconnects to a Turn it had already submitted, without submitting anything again', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-should-not-happen'));
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' });
    rememberTurnId(CONVERSATION, PARTICIPANT, 'native-1', 'turn-resumed');

    const { opened, factory } = recordingEventStreamFactory();
    renderTracer(factory);

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(opened[0].url).toBe('/api/turns/turn-resumed/events');

    // Reconnecting is a read. Nothing mutation-capable is resubmitted.
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('keeps one live stream and its rendered parts across a parent rerender', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();
    const props = {
      csrfToken: 'csrf-token',
      webConversationId: CONVERSATION,
      participantId: PARTICIPANT,
      onTerminalOutcome: () => {},
      createSource: factory,
    };
    const { rerender } = render(<TurnTracer {...props} />);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));
    opened[0].emit(
      'part',
      { turnId: 'turn-1', order: 1, kind: 'text', text: 'Still streaming.', payload: null },
      '100',
    );
    expect(await screen.findByText('Still streaming.')).toBeInTheDocument();

    rerender(<TurnTracer {...props} onTerminalOutcome={() => {}} />);
    await flushAsyncWork();

    expect(opened).toHaveLength(1);
    expect(opened[0].closed).toBe(false);
    expect(screen.getByText('Still streaming.')).toBeInTheDocument();
  });

  it('recovers a live stream after StrictMode\'s development-only mount/cleanup/mount', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-should-not-happen'));
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-1', contentText: 'list stock' });
    rememberTurnId(CONVERSATION, PARTICIPANT, 'native-1', 'turn-resumed');

    const { opened, factory } = recordingEventStreamFactory();
    render(
      <StrictMode>
        <TurnTracer
          csrfToken="csrf-token"
          webConversationId={CONVERSATION}
          participantId={PARTICIPANT}
          onTerminalOutcome={() => {}}
          createSource={factory}
        />
      </StrictMode>,
    );

    // StrictMode's simulated mount/cleanup/mount may close the stream the first pass opened - that
    // is the cleanup working - but it must not leave the resumed Turn with no live stream at all.
    await waitFor(() => expect(opened.some((source) => !source.closed)).toBe(true));
    expect(opened.find((source) => !source.closed)?.url).toBe('/api/turns/turn-resumed/events');

    // Reconnecting is a read, StrictMode or not. Nothing mutation-capable is resubmitted.
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('resubmits the very same native message id when it never learned the Turn id', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-recovered'));
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-lost', contentText: 'list stock' });

    const { opened, factory } = recordingEventStreamFactory();
    renderTracer(factory);

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body));

    // The same idempotency key, so the server converges on the one Turn it may already have recorded
    // rather than doing the work twice.
    expect(body.nativeMessageId).toBe('native-lost');
    await waitFor(() => expect(opened[0].url).toBe('/api/turns/turn-recovered/events'));
  });

  it('renders and settles an already-recorded Outcome returned by a fresh submission', async () => {
    const fetchMock = stubFetch(() => alreadyAnsweredResponse('turn-recorded'));
    const onTerminalOutcome = vi.fn();
    const { opened, factory } = recordingEventStreamFactory();
    render(
      <TurnTracer
        csrfToken="csrf-token"
        webConversationId={CONVERSATION}
        participantId={PARTICIPANT}
        onTerminalOutcome={onTerminalOutcome}
        createSource={factory}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByText('The recorded answer.')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(opened).toHaveLength(0);
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull();
    expect(onTerminalOutcome).toHaveBeenCalledTimes(1);
  });

  it('renders and settles an already-recorded Outcome returned while resuming a lost response', async () => {
    const fetchMock = stubFetch(() => alreadyAnsweredResponse('turn-recorded'));
    rememberSubmission(CONVERSATION, PARTICIPANT, {
      nativeMessageId: 'native-lost',
      contentText: 'list stock',
    });

    const onTerminalOutcome = vi.fn();
    const { opened, factory } = recordingEventStreamFactory();
    render(
      <TurnTracer
        csrfToken="csrf-token"
        webConversationId={CONVERSATION}
        participantId={PARTICIPANT}
        onTerminalOutcome={onTerminalOutcome}
        createSource={factory}
      />,
    );

    expect(await screen.findByText('The recorded answer.')).toBeInTheDocument();
    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body));
    expect(body.nativeMessageId).toBe('native-lost');
    expect(opened).toHaveLength(0);
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull();
    expect(onTerminalOutcome).toHaveBeenCalledTimes(1);
  });

  it('does not resubmit a confirmation whose token was never persisted, and says so plainly', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-should-not-happen'));
    // rememberSubmission itself redacts content containing a well-formed token to null - this is
    // exactly the record a real confirmation submission whose response was lost would leave behind.
    rememberSubmission(CONVERSATION, PARTICIPANT, {
      nativeMessageId: 'native-confirm',
      contentText: `confirm ${FAKE_TOKEN}`,
    });

    const { opened, factory } = recordingEventStreamFactory();
    renderTracer(factory);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'A confirmation was submitted but could not be resumed automatically. Check the current Inventory state before trying again.',
    );

    // There is nothing safe to resubmit - the token was deliberately never kept - so nothing is sent
    // and no stream is ever opened for it.
    expect(fetchMock).not.toHaveBeenCalled();
    expect(opened).toHaveLength(0);
    await waitFor(() => expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull());
  });

  it('clears the in-flight record when a normal submit is definitively rejected (e.g. 400)', async () => {
    const fetchMock = stubFetch(() => new Response(null, { status: 400 }));

    const { factory } = recordingEventStreamFactory();
    renderTracer(factory);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Submitting the Turn failed with status 400.');
    await waitFor(() => expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull());
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('clears a stored lost-response submission when its resubmission is definitively rejected, and does not retry it on a later mount', async () => {
    const fetchMock = stubFetch(() => new Response(null, { status: 400 }));
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-lost', contentText: 'list stock' });

    const { factory } = recordingEventStreamFactory();
    const { unmount } = render(
      <TurnTracer
        csrfToken="csrf-token"
        webConversationId={CONVERSATION}
        participantId={PARTICIPANT}
        onTerminalOutcome={() => {}}
        createSource={factory}
      />,
    );

    await screen.findByRole('alert');
    await waitFor(() => expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull());
    expect(fetchMock).toHaveBeenCalledTimes(1);

    unmount();

    // A later mount - a refresh, or reopening the tab - is a fresh component instance with nothing
    // left to resume, so it must not resubmit the very same, permanently rejected content again.
    const { factory: laterFactory } = recordingEventStreamFactory();
    render(
      <TurnTracer
        csrfToken="csrf-token"
        webConversationId={CONVERSATION}
        participantId={PARTICIPANT}
        onTerminalOutcome={() => {}}
        createSource={laterFactory}
      />,
    );
    await flushAsyncWork();

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('keeps the in-flight record when a normal submit fails at the network level, not the server', async () => {
    const fetchMock = vi.fn(() => Promise.reject(new TypeError('Failed to fetch')));
    vi.stubGlobal('fetch', fetchMock);

    const { factory } = recordingEventStreamFactory();
    renderTracer(factory);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Failed to fetch');
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).not.toBeNull();
  });

  it('keeps the in-flight record when a normal submit is rejected with a retryable status (e.g. 429)', async () => {
    const fetchMock = stubFetch(() => new Response(null, { status: 429 }));

    const { factory } = recordingEventStreamFactory();
    renderTracer(factory);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Submitting the Turn failed with status 429.');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).not.toBeNull();
  });

  it('aborts before sending when browser storage is unavailable, and stays usable', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-should-not-happen'));
    const spy = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Storage is disabled', 'SecurityError');
    });

    try {
      const { factory } = recordingEventStreamFactory();
      renderTracer(factory);

      await userEvent.click(screen.getByRole('button', { name: 'Send' }));

      expect(await screen.findByRole('alert')).toHaveTextContent(
        'Browser storage is unavailable, so this message was not sent - safe recovery cannot be guaranteed without it. Try again once storage is available.',
      );
      expect(fetchMock).not.toHaveBeenCalled();
      expect(screen.getByRole('button', { name: 'Send' })).not.toBeDisabled();
    } finally {
      spy.mockRestore();
    }
  });

  it('keeps watching the current Turn when storing a newer submission fails', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();
    renderTracer(factory);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));
    const currentStream = opened[0];

    const spy = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Storage is disabled', 'SecurityError');
    });

    try {
      await userEvent.click(screen.getByRole('button', { name: 'Send' }));

      expect(await screen.findByRole('alert')).toHaveTextContent(
        'Browser storage is unavailable, so this message was not sent - safe recovery cannot be guaranteed without it. Try again once storage is available.',
      );
      expect(fetchMock).toHaveBeenCalledTimes(1);
      expect(currentStream.closed).toBe(false);

      currentStream.emit(
        'outcome',
        {
          turnId: 'turn-1',
          status: 'completed',
          category: 'completed',
          code: 'echo',
          summary: 'The original answer still arrives.',
          deliveries: [],
        },
        '1000000',
      );

      expect(await screen.findByText('The original answer still arrives.')).toBeInTheDocument();
      expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull();
    } finally {
      spy.mockRestore();
    }
  });

  it('forgets the in-flight Turn once it has an answer', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));

    opened[0].emit(
      'outcome',
      { turnId: 'turn-1', status: 'completed', category: 'completed', code: 'echo', summary: 'Hi.', deliveries: [] },
      '1000000',
    );

    await waitFor(() => expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toBeNull());
  });

  it('keeps a newer Turn B intact when a superseded Turn A\'s streamed Outcome arrives after B was submitted', async () => {
    let resolveB: (response: Response) => void = () => {};
    const pendingB = new Promise<Response>((resolve) => {
      resolveB = resolve;
    });

    let callCount = 0;
    const fetchMock = vi.fn(() => {
      callCount += 1;
      return callCount === 1 ? Promise.resolve(acceptedResponse('turn-A')) : pendingB;
    });
    vi.stubGlobal('fetch', fetchMock);

    const { opened, factory } = recordingEventStreamFactory();
    renderTracer(factory);

    // A is submitted and streaming.
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));
    const streamA = opened[0];

    // B is submitted before A ever answers. Its POST is still pending.
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));

    const storedWhileBPending = readInFlightTurn(CONVERSATION, PARTICIPANT);
    expect(storedWhileBPending?.turnId).toBeNull();
    expect(storedWhileBPending?.nativeMessageId).toBeDefined();

    // A's own stream reports its terminal Outcome only now, after B already exists. It must be
    // ignored entirely - not rendered, and not allowed to clear or touch B's stored record.
    streamA.emit(
      'outcome',
      {
        turnId: 'turn-A',
        status: 'completed',
        category: 'completed',
        code: 'a.echo',
        summary: 'A done.',
        deliveries: [],
      },
      '1000000',
    );
    // Flushed explicitly: `emit` is a raw synchronous call outside any React event, so a state
    // update it triggered still needs a tick to reach the DOM before a synchronous query could see
    // it - without this, an assertion that it never arrives would pass for the wrong reason.
    await flushAsyncWork();

    expect(screen.queryByText('a.echo')).not.toBeInTheDocument();
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual(storedWhileBPending);

    // B's own response now arrives and associates correctly.
    resolveB(acceptedResponse('turn-B'));
    await waitFor(() =>
      expect(opened.some((source) => source.url === '/api/turns/turn-B/events' && !source.closed)).toBe(true),
    );
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)?.turnId).toBe('turn-B');
  });

  it('picks up a Turn another tab of the same browser profile started', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    expect(opened).toHaveLength(0);

    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-other-tab', contentText: 'list stock' });
    rememberTurnId(CONVERSATION, PARTICIPANT, 'native-other-tab', 'turn-other-tab');
    window.dispatchEvent(
      new StorageEvent('storage', { key: `mca.conversation.${CONVERSATION}.${PARTICIPANT}`, newValue: 'changed' }),
    );

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(opened[0].url).toBe('/api/turns/turn-other-tab/events');
  });

  it('never resubmits when the parent re-renders before it has learned the Turn id', async () => {
    let resolveSubmission: (response: Response) => void = () => {};
    const pending = new Promise<Response>((resolve) => {
      resolveSubmission = resolve;
    });

    const fetchMock = vi.fn(() => pending);
    vi.stubGlobal('fetch', fetchMock);

    // The one dangerous window: a stored submission whose response was never seen, so the component
    // is mid-resubmit and `turnId` is still null.
    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-lost', contentText: 'list stock' });

    const { opened, factory } = recordingEventStreamFactory();
    const props = {
      csrfToken: 'csrf-token',
      webConversationId: CONVERSATION,
      participantId: PARTICIPANT,
      onTerminalOutcome: () => {},
      createSource: factory,
    };

    const { rerender } = render(<TurnTracer {...props} />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    // A parent re-render with fresh callback identities - which any unmemoized parent produces on
    // every render - must not make this component submit mutation-capable work a second time.
    rerender(<TurnTracer {...props} onTerminalOutcome={() => {}} />);

    resolveSubmission(acceptedResponse('turn-recovered'));

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('still resubmits only once when it never learned the Turn id, under StrictMode and a parent re-render together', async () => {
    let resolveSubmission: (response: Response) => void = () => {};
    const pending = new Promise<Response>((resolve) => {
      resolveSubmission = resolve;
    });

    const fetchMock = vi.fn(() => pending);
    vi.stubGlobal('fetch', fetchMock);

    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-lost', contentText: 'list stock' });

    const { opened, factory } = recordingEventStreamFactory();
    const props = {
      csrfToken: 'csrf-token',
      webConversationId: CONVERSATION,
      participantId: PARTICIPANT,
      onTerminalOutcome: () => {},
      createSource: factory,
    };

    // StrictMode's own mount/cleanup/mount happens first and alone must not resubmit a second
    // time; the later parent re-render (any unmemoized parent's ordinary behaviour) must not either.
    const { rerender } = render(
      <StrictMode>
        <TurnTracer {...props} />
      </StrictMode>,
    );
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    rerender(
      <StrictMode>
        <TurnTracer {...props} onTerminalOutcome={() => {}} />
      </StrictMode>,
    );

    resolveSubmission(acceptedResponse('turn-recovered'));

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does not act on a submit response that arrives after the component has really unmounted', async () => {
    let resolveSubmission: (response: Response) => void = () => {};
    const pending = new Promise<Response>((resolve) => {
      resolveSubmission = resolve;
    });

    const fetchMock = vi.fn(() => pending);
    vi.stubGlobal('fetch', fetchMock);

    const onTerminalOutcome = vi.fn();
    const { opened, factory } = recordingEventStreamFactory();
    const { unmount } = render(
      <TurnTracer
        csrfToken="csrf-token"
        webConversationId={CONVERSATION}
        participantId={PARTICIPANT}
        onTerminalOutcome={onTerminalOutcome}
        createSource={factory}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    const storedBeforeUnmount = readInFlightTurn(CONVERSATION, PARTICIPANT);
    expect(storedBeforeUnmount?.turnId).toBeNull();

    // A real unmount - not StrictMode's simulated one, which always flips the mounted guard back to
    // true before any awaited response could arrive.
    unmount();

    resolveSubmission(acceptedResponse('turn-after-unmount'));
    await flushAsyncWork();

    // Nobody is left to watch a stream for a response nobody is left to receive.
    expect(opened).toHaveLength(0);
    expect(onTerminalOutcome).not.toHaveBeenCalled();
    // Exactly as it was at the moment of unmount - neither cleared nor stamped with the late Turn id.
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual(storedBeforeUnmount);
  });

  it('does not act on a resumed (lost-response) submission\'s response that arrives after the component has really unmounted', async () => {
    let resolveSubmission: (response: Response) => void = () => {};
    const pending = new Promise<Response>((resolve) => {
      resolveSubmission = resolve;
    });

    const fetchMock = vi.fn(() => pending);
    vi.stubGlobal('fetch', fetchMock);

    rememberSubmission(CONVERSATION, PARTICIPANT, { nativeMessageId: 'native-lost', contentText: 'list stock' });

    const onTerminalOutcome = vi.fn();
    const { opened, factory } = recordingEventStreamFactory();
    const { unmount } = render(
      <TurnTracer
        csrfToken="csrf-token"
        webConversationId={CONVERSATION}
        participantId={PARTICIPANT}
        onTerminalOutcome={onTerminalOutcome}
        createSource={factory}
      />,
    );

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    const storedBeforeUnmount = readInFlightTurn(CONVERSATION, PARTICIPANT);

    unmount();

    resolveSubmission(acceptedResponse('turn-recovered-after-unmount'));
    await flushAsyncWork();

    expect(opened).toHaveLength(0);
    expect(onTerminalOutcome).not.toHaveBeenCalled();
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual(storedBeforeUnmount);
  });

  it('closes an already-open stream on unmount, so a later event on it changes nothing', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const onTerminalOutcome = vi.fn();
    const { opened, factory } = recordingEventStreamFactory();
    const { unmount } = render(
      <TurnTracer
        csrfToken="csrf-token"
        webConversationId={CONVERSATION}
        participantId={PARTICIPANT}
        onTerminalOutcome={onTerminalOutcome}
        createSource={factory}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));
    const stream = opened[0];
    expect(stream.closed).toBe(false);

    const storedBeforeUnmount = readInFlightTurn(CONVERSATION, PARTICIPANT);
    expect(storedBeforeUnmount?.turnId).toBe('turn-1');

    unmount();

    // The cleanup this test exists to pin down: without it, this stream would still be open from
    // openTurnStream's own point of view, and the emit below would still reach its handlers.
    expect(stream.closed).toBe(true);

    stream.emit(
      'outcome',
      { turnId: 'turn-1', status: 'completed', category: 'completed', code: 'echo', summary: 'Hi.', deliveries: [] },
      '1000000',
    );

    expect(onTerminalOutcome).not.toHaveBeenCalled();
    expect(readInFlightTurn(CONVERSATION, PARTICIPANT)).toEqual(storedBeforeUnmount);
  });

  it('never renders a control that would change a quantity directly', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { factory } = recordingEventStreamFactory();

    renderTracer(factory);

    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByRole('button').map((b) => b.textContent)).toEqual(['Send']));
  });
});
