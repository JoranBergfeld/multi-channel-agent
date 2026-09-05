import { StrictMode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { recordingEventStreamFactory } from './testing/fakeEventSource';
import { rememberSubmission, rememberTurnId, readInFlightTurn } from './conversationStorage';
import TurnTracer from './TurnTracer';

const CONVERSATION = 'web-conversation-1';

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

function renderTracer(createSource: ReturnType<typeof recordingEventStreamFactory>['factory']) {
  return render(
    <TurnTracer
      csrfToken="csrf-token"
      webConversationId={CONVERSATION}
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
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' });
    rememberTurnId(CONVERSATION, 'native-1', 'turn-resumed');

    const { opened, factory } = recordingEventStreamFactory();
    renderTracer(factory);

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(opened[0].url).toBe('/api/turns/turn-resumed/events');

    // Reconnecting is a read. Nothing mutation-capable is resubmitted.
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('recovers a live stream after StrictMode\'s development-only mount/cleanup/mount', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-should-not-happen'));
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' });
    rememberTurnId(CONVERSATION, 'native-1', 'turn-resumed');

    const { opened, factory } = recordingEventStreamFactory();
    render(
      <StrictMode>
        <TurnTracer
          csrfToken="csrf-token"
          webConversationId={CONVERSATION}
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
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-lost', contentText: 'list stock' });

    const { opened, factory } = recordingEventStreamFactory();
    renderTracer(factory);

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body));

    // The same idempotency key, so the server converges on the one Turn it may already have recorded
    // rather than doing the work twice.
    expect(body.nativeMessageId).toBe('native-lost');
    await waitFor(() => expect(opened[0].url).toBe('/api/turns/turn-recovered/events'));
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

    await waitFor(() => expect(readInFlightTurn(CONVERSATION)).toBeNull());
  });

  it('picks up a Turn another tab of the same browser profile started', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    expect(opened).toHaveLength(0);

    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-other-tab', contentText: 'list stock' });
    rememberTurnId(CONVERSATION, 'native-other-tab', 'turn-other-tab');
    window.dispatchEvent(
      new StorageEvent('storage', { key: `mca.conversation.${CONVERSATION}`, newValue: 'changed' }),
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
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-lost', contentText: 'list stock' });

    const { opened, factory } = recordingEventStreamFactory();
    const props = {
      csrfToken: 'csrf-token',
      webConversationId: CONVERSATION,
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

    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-lost', contentText: 'list stock' });

    const { opened, factory } = recordingEventStreamFactory();
    const props = {
      csrfToken: 'csrf-token',
      webConversationId: CONVERSATION,
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

  it('never renders a control that would change a quantity directly', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { factory } = recordingEventStreamFactory();

    renderTracer(factory);

    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByRole('button').map((b) => b.textContent)).toEqual(['Send']));
  });
});
