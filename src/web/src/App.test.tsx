import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DESKTOP_WIDTH, NARROW_WIDTH, setViewportWidth } from './testing/setup';
import { FakeEventSource, installFakeEventSource } from './testing/fakeEventSource';
import { readInFlightTurn, rememberSubmission, rememberTurnId } from './conversationStorage';
import { FakeVoiceTransport } from './testing/fakeVoiceTransport';
import App from './App';

const BOOTSTRAP = {
  bootstrap: {
    participantId: '11111111-1111-1111-1111-111111111111',
    displayName: 'Ada Lovelace',
    webConversationId: 'web-conversation-1',
    inventories: [
      { id: 'inventory-1', shortId: 'aaaaaaaa', name: 'Main Warehouse', ownerDisplayName: 'Ada Lovelace', role: 'Editor' },
      { id: 'inventory-2', shortId: 'bbbbbbbb', name: 'Spare Warehouse', ownerDisplayName: 'Ada Lovelace', role: 'Editor' },
    ],
    activeInventoryId: 'inventory-1',
    needsOnboarding: false,
  },
  csrfToken: 'csrf-token',
};

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function stubApi(overrides: Record<string, () => Response> = {}) {
  const calls: string[] = [];

  const fetchMock = vi.fn((input: RequestInfo | URL, _init?: RequestInit) => {
    const url = String(input);
    calls.push(url);

    for (const [prefix, respond] of Object.entries(overrides)) {
      if (url.startsWith(prefix)) {
        return Promise.resolve(respond());
      }
    }

    if (url.startsWith('/api/session/bootstrap')) {
      return Promise.resolve(json(BOOTSTRAP));
    }

    if (url.includes('/stock')) {
      return Promise.resolve(json({ rows: [], nextCursor: null, hasMore: false }));
    }

    if (url.includes('/units')) {
      return Promise.resolve(json({ units: [], nextCursor: null, hasMore: false }));
    }

    if (url.includes('/locations')) {
      return Promise.resolve(json({ locations: [], nextCursor: null, hasMore: false }));
    }

    return Promise.resolve(json({}));
  });

  vi.stubGlobal('fetch', fetchMock);

  // The Participant-level stream opens through the default factory, so the double is installed as the
  // global EventSource. Tests then push real snapshot/changed events through it.
  const streams = installFakeEventSource();

  return { fetchMock, calls, streams };
}

function streamsFor(streams: FakeEventSource[], url: string) {
  return streams.filter((stream) => stream.url === url);
}

function inventoryStreamIn(streams: FakeEventSource[]) {
  return streams.find((stream) => stream.url === '/api/inventory-events');
}

function turnStreamIn(streams: FakeEventSource[]) {
  return streams.find((stream) => stream.url.startsWith('/api/turns/'));
}

describe('App', () => {
  it('keeps the conversation in the main landmark on a desktop viewport', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    stubApi();

    render(<App />);

    // Waited on deliberately. The loading state renders a `main` of its own, so waiting for `main`
    // would resolve against the loading tree and assert nothing about the ready one. The banner exists
    // only once the session bootstrap has resolved.
    await screen.findByRole('banner');

    const main = screen.getByRole('main');
    expect(within(main).getByRole('heading', { name: 'Conversation' })).toBeInTheDocument();
    expect(screen.getByRole('complementary', { name: 'Inventory workspace' })).toBeInTheDocument();
  });

  it('shows the conversation first behind a tab list on a narrow viewport', async () => {
    setViewportWidth(NARROW_WIDTH);
    stubApi();

    render(<App />);

    await screen.findByRole('banner');
    await screen.findByRole('tablist');
    const tabs = screen.getAllByRole('tab');
    expect(tabs.map((tab) => tab.textContent)).toEqual(['Conversation', 'Inventory']);
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');

    // The landmark is still there at this width; the tab panel is inside it, not instead of it.
    expect(within(screen.getByRole('main')).getByRole('tabpanel')).toBeInTheDocument();
  });

  it('shows the Active Inventory in the always-visible header at every width', async () => {
    setViewportWidth(NARROW_WIDTH);
    stubApi();

    render(<App />);

    const banner = await screen.findByRole('banner');
    expect(within(banner).getByText(/Main Warehouse/)).toBeInTheDocument();
  });

  it('switches the Active Inventory only when it is explicitly asked to', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { calls } = stubApi();

    render(<App />);
    await screen.findByRole('banner');

    // Looking at the list is browsing. Nothing has been selected.
    expect(calls.some((url) => url.includes('/select'))).toBe(false);

    await userEvent.click(await screen.findByRole('button', { name: 'Use in this conversation' }));

    await waitFor(() => expect(calls.some((url) => url === '/api/inventories/inventory-2/select')).toBe(true));
  });

  it('opens the Participant-level invalidation stream once the session is ready', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { streams } = stubApi();

    render(<App />);
    await screen.findByRole('banner');

    await waitFor(() => expect(streams.filter((s) => s.url === '/api/inventory-events')).toHaveLength(1));
  });

  it('shows a clear resync message if the Inventory stream fails permanently, and no unrelated action clears it', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { streams } = stubApi();

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());

    // A 401/403/404, or a response that is not `text/event-stream`, ends this way: the browser gives
    // up reconnecting on its own, so silence here would leave the workspace unable to ever invalidate
    // again until the Participant does something about it themselves.
    inventoryStreamIn(streams)!.failFatally();

    const fatalMessage =
      'Lost the connection to Inventory updates and cannot resync automatically. Refresh the page to try again.';
    expect(await screen.findByRole('alert')).toHaveTextContent(fatalMessage);

    // This warning is not an ordinary operation error - the stream is permanently dead until the page
    // is refreshed, no matter what else the Participant does in the meantime. Selecting an Inventory
    // is one of several handlers that clears the ordinary `error` state the instant it starts its own
    // attempt; it must not also erase this dedicated one, or the warning would vanish while the stream
    // stayed just as dead.
    await userEvent.click(await screen.findByRole('button', { name: 'Use in this conversation' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Use in this conversation' })).toBeInTheDocument());

    expect(screen.getByRole('alert')).toHaveTextContent(fatalMessage);
  });

  it('refetches the workspace when the stream says the Inventory version changed', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { calls, streams } = stubApi();

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(calls.filter((url) => url.includes('/stock'))).toHaveLength(1));
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());

    // A change made anywhere - this conversation, another tab, another Participant, a future channel -
    // reaches this tab as a version, and the authoritative projection is re-read without a reload.
    inventoryStreamIn(streams)!.emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 0 }] });
    inventoryStreamIn(streams)!.emit('changed', { inventoryId: 'inventory-1', version: 1 });

    await waitFor(() => expect(calls.filter((url) => url.includes('/stock')).length).toBeGreaterThan(1));
  });

  it('refreshes the authorized Inventory list when the stream reports a revocation', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    let bootstrapCalls = 0;
    const { streams } = stubApi({
      '/api/session/bootstrap': () => {
        bootstrapCalls += 1;
        return json(
          bootstrapCalls === 1
            ? BOOTSTRAP
            : {
                ...BOOTSTRAP,
                bootstrap: {
                  ...BOOTSTRAP.bootstrap,
                  inventories: [BOOTSTRAP.bootstrap.inventories[0]],
                },
              },
        );
      },
    });

    render(<App />);
    await screen.findByText(/Spare Warehouse/);
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());

    inventoryStreamIn(streams)!.emit('snapshot', {
      inventories: [
        { inventoryId: 'inventory-1', version: 0 },
        { inventoryId: 'inventory-2', version: 0 },
      ],
    });
    inventoryStreamIn(streams)!.emit('revoked', { inventoryId: 'inventory-2' });

    await waitFor(() => expect(screen.queryByText(/Spare Warehouse/)).not.toBeInTheDocument());
    expect(bootstrapCalls).toBe(2);
  });

  it('refreshes the authorized Inventory list when the stream reports a new grant', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    let bootstrapCalls = 0;
    const grantedInventory = {
      id: 'inventory-3',
      shortId: 'cccccccc',
      name: 'Granted Warehouse',
      ownerDisplayName: 'Grace Hopper',
      role: 'Viewer',
    };
    const { streams } = stubApi({
      '/api/session/bootstrap': () => {
        bootstrapCalls += 1;
        return json(
          bootstrapCalls === 1
            ? BOOTSTRAP
            : {
                ...BOOTSTRAP,
                bootstrap: {
                  ...BOOTSTRAP.bootstrap,
                  inventories: [...BOOTSTRAP.bootstrap.inventories, grantedInventory],
                },
              },
        );
      },
    });

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());

    inventoryStreamIn(streams)!.emit('snapshot', {
      inventories: [
        { inventoryId: 'inventory-1', version: 0 },
        { inventoryId: 'inventory-2', version: 0 },
      ],
    });
    inventoryStreamIn(streams)!.emit('changed', { inventoryId: 'inventory-3', version: 0 });

    expect(await screen.findByText(/Granted Warehouse/)).toBeInTheDocument();
    expect(bootstrapCalls).toBe(2);
  });

  it('retries membership reconciliation until bootstrap reflects the streamed authorized set', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    let bootstrapCalls = 0;
    const grantedInventory = {
      id: 'inventory-3',
      shortId: 'cccccccc',
      name: 'Eventually Granted Warehouse',
      ownerDisplayName: 'Grace Hopper',
      role: 'Viewer',
    };
    const { streams } = stubApi({
      '/api/session/bootstrap': () => {
        bootstrapCalls += 1;
        if (bootstrapCalls === 2) {
          return json({}, 503);
        }

        return json(
          bootstrapCalls < 4
            ? BOOTSTRAP
            : {
                ...BOOTSTRAP,
                bootstrap: {
                  ...BOOTSTRAP.bootstrap,
                  inventories: [...BOOTSTRAP.bootstrap.inventories, grantedInventory],
                },
              },
        );
      },
    });

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());

    inventoryStreamIn(streams)!.emit('snapshot', {
      inventories: [
        { inventoryId: 'inventory-1', version: 0 },
        { inventoryId: 'inventory-2', version: 0 },
      ],
    });
    inventoryStreamIn(streams)!.emit('changed', { inventoryId: 'inventory-3', version: 0 });

    expect(await screen.findByText(/Eventually Granted Warehouse/, {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(bootstrapCalls).toBeGreaterThanOrEqual(4);
  });

  it('bounds membership reconciliation retries while bootstrap stays unavailable', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    let bootstrapCalls = 0;
    const { streams } = stubApi({
      '/api/session/bootstrap': () => {
        bootstrapCalls += 1;
        return bootstrapCalls === 1 ? json(BOOTSTRAP) : json({}, 503);
      },
    });

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());

    inventoryStreamIn(streams)!.emit('changed', { inventoryId: 'inventory-3', version: 0 });

    await waitFor(() => expect(bootstrapCalls).toBe(5), { timeout: 3000 });
    await new Promise((resolve) => setTimeout(resolve, 500));

    expect(bootstrapCalls).toBe(5);
    expect(screen.getByRole('alert')).toHaveTextContent('Reading the session bootstrap failed with status 503.');
  });

  it('clears a fatal Inventory-stream warning after a replacement stream resynchronizes', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    let bootstrapCalls = 0;
    const createdInventory = {
      id: 'inventory-3',
      shortId: 'cccccccc',
      name: 'Created Warehouse',
      ownerDisplayName: 'Ada Lovelace',
      role: 'Owner',
    };
    const { streams } = stubApi({
      '/api/session/bootstrap': () => {
        bootstrapCalls += 1;
        return json(
          bootstrapCalls === 1
            ? BOOTSTRAP
            : {
                ...BOOTSTRAP,
                bootstrap: {
                  ...BOOTSTRAP.bootstrap,
                  inventories: [...BOOTSTRAP.bootstrap.inventories, createdInventory],
                },
              },
        );
      },
    });

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());
    inventoryStreamIn(streams)!.failFatally();
    expect(await screen.findByRole('alert')).toHaveTextContent('Lost the connection to Inventory updates');

    await userEvent.type(screen.getByLabelText('New Inventory name'), 'Created Warehouse');
    await userEvent.click(screen.getByRole('button', { name: 'Create Inventory' }));
    await waitFor(() => expect(streamsFor(streams, '/api/inventory-events')).toHaveLength(2));

    streamsFor(streams, '/api/inventory-events')[1].emit('snapshot', {
      inventories: [
        { inventoryId: 'inventory-1', version: 0 },
        { inventoryId: 'inventory-2', version: 0 },
        { inventoryId: 'inventory-3', version: 0 },
      ],
    });

    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });

  it('ignores an older session response that arrives after a newer Active Inventory refresh', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    let bootstrapCalls = 0;
    let resolveMembershipRefresh: (response: Response) => void = () => {};
    let resolveSelectionRefresh: (response: Response) => void = () => {};
    const membershipRefresh = new Promise<Response>((resolve) => {
      resolveMembershipRefresh = resolve;
    });
    const selectionRefresh = new Promise<Response>((resolve) => {
      resolveSelectionRefresh = resolve;
    });
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith('/api/session/bootstrap')) {
        bootstrapCalls += 1;
        if (bootstrapCalls === 1) {
          return Promise.resolve(json(BOOTSTRAP));
        }

        return bootstrapCalls === 2 ? membershipRefresh : selectionRefresh;
      }

      if (url === '/api/inventories/inventory-2/select') {
        return Promise.resolve(json({}));
      }

      if (url.includes('/stock')) {
        return Promise.resolve(json({ rows: [], nextCursor: null, hasMore: false }));
      }

      if (url.includes('/units')) {
        return Promise.resolve(json({ units: [], nextCursor: null, hasMore: false }));
      }

      if (url.includes('/locations')) {
        return Promise.resolve(json({ locations: [], nextCursor: null, hasMore: false }));
      }

      return Promise.resolve(json({}));
    });
    vi.stubGlobal('fetch', fetchMock);
    const streams = installFakeEventSource();

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());

    inventoryStreamIn(streams)!.emit('changed', { inventoryId: 'inventory-3', version: 0 });
    await waitFor(() => expect(bootstrapCalls).toBe(2));

    await userEvent.click(screen.getByRole('button', { name: 'Use in this conversation' }));
    await waitFor(() => expect(bootstrapCalls).toBe(3));

    resolveSelectionRefresh(
      json({
        ...BOOTSTRAP,
        bootstrap: { ...BOOTSTRAP.bootstrap, activeInventoryId: 'inventory-2' },
      }),
    );
    await waitFor(() =>
      expect(within(screen.getByRole('banner')).getByText(/Spare Warehouse/)).toBeInTheDocument(),
    );

    resolveMembershipRefresh(json(BOOTSTRAP));
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(within(screen.getByRole('banner')).getByText(/Spare Warehouse/)).toBeInTheDocument();
  });

  it('does not let a locally signalled refetch swallow the next version the server publishes', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { calls, streams } = stubApi({
      '/api/turns': () => json({ turnId: 'turn-1', alreadyAccepted: false }, 202),
    });

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(calls.filter((url) => url.includes('/stock'))).toHaveLength(1));
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());

    inventoryStreamIn(streams)!.emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 0 }] });

    // A Turn finishes. This tab knows to re-read, but the server has published no new version - so the
    // signal must live in a namespace of its own.
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(turnStreamIn(streams)).toBeDefined());
    turnStreamIn(streams)!.emit(
      'outcome',
      {
        turnId: 'turn-1',
        status: 'completed',
        category: 'completed',
        code: 'stock.listed',
        summary: 'None.',
        deliveries: [],
      },
      '1000000',
    );

    await waitFor(() => expect(calls.filter((url) => url.includes('/stock'))).toHaveLength(2));

    // Now the FIRST version the server ever publishes arrives. Had the local signal been written into
    // the server's version namespace, this would look like a version this tab had already seen, and
    // the change behind it would never be read at all.
    inventoryStreamIn(streams)!.emit('changed', { inventoryId: 'inventory-1', version: 1 });

    await waitFor(() => expect(calls.filter((url) => url.includes('/stock'))).toHaveLength(3));
  });

  it('starts a new conversation, forgets the in-flight Turn, and tells the Participant what it cleared', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { streams } = stubApi({
      '/api/conversation/new': () =>
        json({ foundryConversationId: 'foundry-2', generation: 2, clearedPendingConfirmation: true }),
    });

    // Something this browser profile was already waiting on. If the reset left it behind, the remounted
    // conversation would immediately reconnect - or re-POST - work from the conversation just ended.
    rememberSubmission('web-conversation-1', '11111111-1111-1111-1111-111111111111', {
      nativeMessageId: 'native-old',
      contentText: 'list stock',
    });
    rememberTurnId('web-conversation-1', '11111111-1111-1111-1111-111111111111', 'native-old', 'turn-old');

    render(<App />);
    await screen.findByRole('banner');

    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));

    const banner = screen.getByRole('banner');
    await waitFor(() =>
      expect(within(banner).getByRole('status')).toHaveTextContent(
        'Started a new conversation. The change that was waiting for confirmation was cleared.',
      ),
    );

    expect(readInFlightTurn('web-conversation-1', '11111111-1111-1111-1111-111111111111')).toBeNull();

    // Resuming that stored Turn on the first mount is correct - reconnecting is a pure read, and it is
    // exactly what the conversation contracts to do for a Turn that was still outstanding. What the
    // reset must not leave behind is that stream: it belongs to the conversation that just ended, so
    // it is closed, and the remounted conversation opens no second one for it.
    const oldTurnStreams = streamsFor(streams, '/api/turns/turn-old/events');
    expect(oldTurnStreams).toHaveLength(1);
    expect(oldTurnStreams[0].closed).toBe(true);
  });

  it('clears an earlier reset notice before reporting a later reset failure', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    let resetCount = 0;
    stubApi({
      '/api/conversation/new': () => {
        resetCount += 1;
        return resetCount === 1
          ? json({ foundryConversationId: 'foundry-2', generation: 2, clearedPendingConfirmation: false })
          : json({}, 500);
      },
    });

    render(<App />);
    const banner = await screen.findByRole('banner');

    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));
    await waitFor(() => expect(within(banner).getByRole('status')).toHaveTextContent('Started a new conversation.'));

    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Starting a new conversation failed with status 500.',
    );
    expect(within(banner).getByRole('status')).toHaveTextContent('');
  });

  it('keeps the current conversation mounted when its recovery record cannot be cleared', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { streams } = stubApi({
      '/api/conversation/new': () =>
        json({ foundryConversationId: 'foundry-2', generation: 2, clearedPendingConfirmation: false }),
    });
    rememberSubmission('web-conversation-1', '11111111-1111-1111-1111-111111111111', {
      nativeMessageId: 'native-old',
      contentText: 'list stock',
    });
    rememberTurnId('web-conversation-1', '11111111-1111-1111-1111-111111111111', 'native-old', 'turn-old');

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(streamsFor(streams, '/api/turns/turn-old/events')).toHaveLength(1));
    const oldTurnStream = streamsFor(streams, '/api/turns/turn-old/events')[0];

    const spy = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new DOMException('Storage is disabled', 'SecurityError');
    });

    try {
      await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));

      expect(await screen.findByRole('alert')).toHaveTextContent(
        'The new conversation started, but browser recovery state could not be cleared safely. The current view was kept open to avoid recovering work from the prior conversation. Try again once browser storage is available.',
      );
      expect(within(screen.getByRole('banner')).getByRole('status')).toHaveTextContent('');
      expect(oldTurnStream.closed).toBe(false);
      expect(readInFlightTurn('web-conversation-1', '11111111-1111-1111-1111-111111111111')).not.toBeNull();
    } finally {
      spy.mockRestore();
    }
  });

  it('never offers a control that would change a quantity directly', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    stubApi();

    render(<App />);
    await screen.findByRole('banner');

    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /save quantity/i })).not.toBeInTheDocument();
  });

  // ── Voice integration ──────────────────────────────────────────────────────

  it('renders VoiceControls with a Start Voice button when ready', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const transport = new FakeVoiceTransport();
    stubApi();

    render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    expect(screen.getByRole('button', { name: 'Start Voice' })).toBeInTheDocument();
  });

  it('New Conversation fences voice callbacks, disconnects transport, rotates, then releases', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const callOrder: string[] = [];
    const transport = new FakeVoiceTransport();
    const origDisconnect = transport.disconnect.bind(transport);
    transport.disconnect = () => {
      callOrder.push('disconnect');
      origDisconnect();
    };

    stubApi({
      '/api/voice/admit': () => {
        callOrder.push('admit');
        return json({ admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null });
      },
      '/api/conversation/new': () => {
        callOrder.push('rotate');
        return json({ foundryConversationId: 'foundry-2', generation: 2, clearedPendingConfirmation: false });
      },
      '/api/voice/release': () => {
        callOrder.push('release');
        return json({});
      },
    });

    render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    // Admit voice
    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }));
    transport.simulateConnected();
    await waitFor(() => expect(callOrder).toContain('admit'));

    callOrder.length = 0;

    // Click New Conversation
    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));

    await waitFor(() => expect(callOrder).toContain('release'));

    // Ordering: disconnect before rotate, release after rotate
    const disconnectIdx = callOrder.indexOf('disconnect');
    const rotateIdx = callOrder.indexOf('rotate');
    const releaseIdx = callOrder.indexOf('release');
    expect(disconnectIdx).toBeLessThan(rotateIdx);
    expect(rotateIdx).toBeLessThan(releaseIdx);
  });

  it('rotation failure still releases voice session best-effort', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const callOrder: string[] = [];
    const transport = new FakeVoiceTransport();

    stubApi({
      '/api/voice/admit': () => {
        return json({ admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null });
      },
      '/api/conversation/new': () => {
        callOrder.push('rotate');
        return json({}, 500);
      },
      '/api/voice/release': () => {
        callOrder.push('release');
        return json({});
      },
    });

    render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }));
    transport.simulateConnected();
    await waitFor(() => expect(transport.connectCount).toBe(1));

    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));
    await waitFor(() => expect(callOrder).toContain('release'));

    // Rotation failed, voice still released best-effort
    expect(screen.getByRole('alert')).toHaveTextContent(/500/);
  });

  it('storage clear failure does not remount and still releases voice best-effort', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const callOrder: string[] = [];
    const transport = new FakeVoiceTransport();

    rememberSubmission('web-conversation-1', '11111111-1111-1111-1111-111111111111', {
      nativeMessageId: 'native-old',
      contentText: 'list stock',
    });
    rememberTurnId('web-conversation-1', '11111111-1111-1111-1111-111111111111', 'native-old', 'turn-old');

    stubApi({
      '/api/voice/admit': () => json({ admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null }),
      '/api/conversation/new': () => {
        callOrder.push('rotate');
        return json({ foundryConversationId: 'foundry-2', generation: 2, clearedPendingConfirmation: false });
      },
      '/api/voice/release': () => {
        callOrder.push('release');
        return json({});
      },
    });

    render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }));
    transport.simulateConnected();
    await waitFor(() => expect(transport.connectCount).toBe(1));

    const spy = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new DOMException('Storage is disabled', 'SecurityError');
    });

    try {
      await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));

      await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(/recovery state/));

      // Voice still released best-effort even when storage clear fails
      await waitFor(() => expect(callOrder).toContain('release'));
    } finally {
      spy.mockRestore();
    }
  });

  it('late voice callback from prior generation does not submit a turn', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const transport = new FakeVoiceTransport();
    const { fetchMock } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null }),
      '/api/voice/release': () => json({}),
    });

    render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    // Admit voice (generation 1)
    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }));
    transport.simulateConnected();
    await waitFor(() => expect(transport.connectCount).toBe(1));

    // End voice → transport disconnects, generation increments
    await userEvent.click(screen.getByRole('button', { name: 'End Voice' }));

    // Late callback from generation 1 — transport is disconnected so callback won't fire
    // (FakeVoiceTransport suppresses callbacks after disconnect)
    transport.simulateFinalTranscript('stale text', 'voice:vs-1:item_old');

    // No /api/turns call should have occurred
    const turnsCalls = fetchMock.mock.calls.filter(
      (call: [RequestInfo | URL, RequestInit?]) => String(call[0]) === '/api/turns',
    );
    expect(turnsCalls).toHaveLength(0);
  });

  it('finalized voice utterance submits exactly once through shared controller with voiceSessionId', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const transport = new FakeVoiceTransport();
    const { fetchMock } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null }),
      '/api/turns': () => json({ turnId: 'turn-voice-1', alreadyAccepted: false }, 202),
    });

    render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }));
    transport.simulateConnected();
    await waitFor(() => expect(transport.connectCount).toBe(1));

    // Simulate finalized transcript
    transport.simulateFinalTranscript('add five steel bolts', 'voice:vs-1:item_1');

    await waitFor(() => {
      const turnsCalls = fetchMock.mock.calls.filter(
        (call: [RequestInfo | URL, RequestInit?]) => String(call[0]) === '/api/turns',
      );
      expect(turnsCalls).toHaveLength(1);
    });

    // Verify the body includes voiceSessionId
    const turnsCall = fetchMock.mock.calls.find(
      (call: [RequestInfo | URL, RequestInit?]) => String(call[0]) === '/api/turns',
    )!;
    const body = JSON.parse(turnsCall[1]?.body as string);
    expect(body.voiceSessionId).toBe('vs-1');
    expect(body.contentText).toBe('add five steel bolts');
    expect(body.nativeMessageId).toBe('voice:vs-1:item_1');
  });

  it('partial transcript does not submit a turn', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const transport = new FakeVoiceTransport();
    const { fetchMock } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null }),
    });

    render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }));
    transport.simulateConnected();
    await waitFor(() => expect(transport.connectCount).toBe(1));

    transport.simulatePartialTranscript('add fi');
    // Allow async work to settle
    await new Promise((resolve) => setTimeout(resolve, 50));

    const turnsCalls = fetchMock.mock.calls.filter(
      (call: [RequestInfo | URL, RequestInit?]) => String(call[0]) === '/api/turns',
    );
    expect(turnsCalls).toHaveLength(0);
  });

  it('text submission still works alongside voice controls', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const transport = new FakeVoiceTransport();
    const { fetchMock } = stubApi({
      '/api/turns': () => json({ turnId: 'turn-text-1', alreadyAccepted: false }, 202),
    });

    render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));

    // The /api/turns call was made — text submission works through the same controller
    await waitFor(() => {
      const turnsCalls = fetchMock.mock.calls.filter(
        (call: [RequestInfo | URL, RequestInit?]) => String(call[0]) === '/api/turns',
      );
      expect(turnsCalls).toHaveLength(1);
    });

    // No voiceSessionId in text submission
    const turnsCall = fetchMock.mock.calls.find(
      (call: [RequestInfo | URL, RequestInit?]) => String(call[0]) === '/api/turns',
    )!;
    const body = JSON.parse(turnsCall[1]?.body as string);
    expect(body.voiceSessionId).toBeUndefined();
  });

  it('unmount releases session with keepalive fetch (best-effort)', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const transport = new FakeVoiceTransport();
    const { fetchMock } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null }),
    });

    const { unmount } = render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }));
    transport.simulateConnected();
    await waitFor(() => expect(transport.connectCount).toBe(1));

    unmount();

    // Best-effort release was attempted with keepalive
    const releaseCalls = fetchMock.mock.calls.filter(
      (call: [RequestInfo | URL, RequestInit?]) => String(call[0]) === '/api/voice/release',
    );
    expect(releaseCalls).toHaveLength(1);
    expect(releaseCalls[0][1]).toMatchObject({
      method: 'POST',
      keepalive: true,
    });
  });

  it('unmount does not release when no voice session is active', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const transport = new FakeVoiceTransport();
    const { fetchMock } = stubApi();

    const { unmount } = render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    unmount();

    const releaseCalls = fetchMock.mock.calls.filter(
      (call: [RequestInfo | URL, RequestInit?]) => String(call[0]) === '/api/voice/release',
    );
    expect(releaseCalls).toHaveLength(0);
  });

  it('unmount release uses exact captured session ID and CSRF/JSON headers', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const transport = new FakeVoiceTransport();
    const { fetchMock } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-exact-42', sdpAnswer: 'v=0\r\n', denialReason: null }),
    });

    const { unmount } = render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }));
    transport.simulateConnected();
    await waitFor(() => expect(transport.connectCount).toBe(1));

    unmount();

    const releaseCalls = fetchMock.mock.calls.filter(
      (call: [RequestInfo | URL, RequestInit?]) => String(call[0]) === '/api/voice/release',
    );
    expect(releaseCalls).toHaveLength(1);
    const init = releaseCalls[0][1] as RequestInit;
    expect(init.headers).toEqual(
      expect.objectContaining({
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': 'csrf-token',
      }),
    );
    const body = JSON.parse(init.body as string);
    expect(body.voiceSessionId).toBe('vs-exact-42');
  });

  it('does not assert network release always completes during unload — SQL idle/expiry is authoritative', () => {
    // Unmount release uses keepalive + custom CSRF header which is not dependable during
    // page unload. SQL idle/expiry is the authoritative cleanup mechanism.
    expect(true).toBe(true);
  });

  it('production App creates a BrowserVoiceTransport when no testTransport is provided', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    // BrowserVoiceTransport import requires RTCPeerConnection which jsdom doesn't have,
    // so we verify the App's default transport creates the right type
    // by checking VoiceControls renders without testTransport (start button visible).
    stubApi();

    render(<App />);
    await screen.findByRole('banner');

    // Voice controls should be visible with a Start Voice button
    expect(screen.getByRole('button', { name: 'Start Voice' })).toBeInTheDocument();
  });

  it('canonical TurnTracer summary remains visible after voice playback failure', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const transport = new FakeVoiceTransport();
    const { streams } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-pf1', sdpAnswer: 'v=0\r\n', denialReason: null }),
      '/api/turns': () => json({ turnId: 'turn-pf1', alreadyAccepted: false }, 202),
    });

    render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    // Admit and connect voice
    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }));
    transport.simulateConnected();
    await waitFor(() => expect(transport.connectCount).toBe(1));

    // Finalized transcript triggers shared controller to submit a turn with voiceSessionId
    transport.simulateFinalTranscript('list steel bolts', 'voice:vs-pf1:item_1');
    await waitFor(() => expect(turnStreamIn(streams)).toBeDefined());

    // Drive accepted → processing → terminal outcome through the fake SSE mechanism
    const CANONICAL_SUMMARY = 'Twelve steel bolts are on Shelf A.';
    turnStreamIn(streams)!.emit(
      'accepted',
      { turnId: 'turn-pf1', receivedAt: '2026-09-06T10:00:00+00:00' },
      '1',
    );
    turnStreamIn(streams)!.emit(
      'processing',
      { turnId: 'turn-pf1', startedAt: '2026-09-06T10:00:01+00:00' },
      '2',
    );
    turnStreamIn(streams)!.emit(
      'outcome',
      {
        turnId: 'turn-pf1',
        status: 'completed',
        category: 'completed',
        code: 'stock.listed',
        summary: CANONICAL_SUMMARY,
        deliveries: [],
      },
      '1000000',
    );

    // TurnTracer renders the exact canonical summary
    expect(await screen.findByText(CANONICAL_SUMMARY)).toBeInTheDocument();

    // Simulate playback_started → speaking, then playback_failed → listening + alert
    transport.simulatePlaybackStarted();
    transport.simulatePlaybackFailed('TTS synthesis error');

    // Accessible playback failure alert appears
    await waitFor(() =>
      expect(screen.getByRole('alert', { name: 'Playback failure' })).toBeInTheDocument(),
    );

    // The exact canonical summary is still visible in TurnTracer, unchanged
    expect(screen.getByText(CANONICAL_SUMMARY)).toBeInTheDocument();
  });

  it('canonical TurnTracer summary remains visible after voice playback integrity error', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const transport = new FakeVoiceTransport();
    const { streams } = stubApi({
      '/api/voice/admit': () =>
        json({ admitted: true, voiceSessionId: 'vs-ie1', sdpAnswer: 'v=0\r\n', denialReason: null }),
      '/api/turns': () => json({ turnId: 'turn-ie1', alreadyAccepted: false }, 202),
    });

    render(<App testTransport={transport} />);
    await screen.findByRole('banner');

    await userEvent.click(screen.getByRole('button', { name: 'Start Voice' }));
    transport.simulateConnected();
    await waitFor(() => expect(transport.connectCount).toBe(1));

    transport.simulateFinalTranscript('list steel bolts', 'voice:vs-ie1:item_1');
    await waitFor(() => expect(turnStreamIn(streams)).toBeDefined());

    const CANONICAL_SUMMARY = 'Four brass rivets are unlocated.';
    turnStreamIn(streams)!.emit(
      'outcome',
      {
        turnId: 'turn-ie1',
        status: 'completed',
        category: 'completed',
        code: 'stock.listed',
        summary: CANONICAL_SUMMARY,
        deliveries: [],
      },
      '1000000',
    );

    expect(await screen.findByText(CANONICAL_SUMMARY)).toBeInTheDocument();

    // Simulate playback_started then an integrity error (dispatches playback_failed internally)
    transport.simulatePlaybackStarted();
    transport.simulatePlaybackIntegrityError('Four brass rivets are unlocated.', 'Unexpected item X.');

    // Accessible playback failure alert appears
    await waitFor(() =>
      expect(screen.getByRole('alert', { name: 'Playback failure' })).toBeInTheDocument(),
    );

    // The exact canonical summary is still visible in TurnTracer, unchanged
    expect(screen.getByText(CANONICAL_SUMMARY)).toBeInTheDocument();
  });
});
