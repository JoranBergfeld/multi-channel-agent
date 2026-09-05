import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DESKTOP_WIDTH, NARROW_WIDTH, setViewportWidth } from './testing/setup';
import { FakeEventSource, installFakeEventSource } from './testing/fakeEventSource';
import { readInFlightTurn, rememberSubmission, rememberTurnId } from './conversationStorage';
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

  const fetchMock = vi.fn((input: RequestInfo | URL) => {
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

afterEach(() => {
  vi.unstubAllGlobals();
});

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

  it('never offers a control that would change a quantity directly', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    stubApi();

    render(<App />);
    await screen.findByRole('banner');

    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /save quantity/i })).not.toBeInTheDocument();
  });
});
