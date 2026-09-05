import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DESKTOP_WIDTH, NARROW_WIDTH, setViewportWidth } from './testing/setup';
import WorkspacePanel from './WorkspacePanel';

function renderPanel() {
  return render(
    <WorkspacePanel
      conversation={<p>Conversation content</p>}
      workspace={<p>Workspace content</p>}
    />,
  );
}

/** A probe that proves whether it was remounted: its count only ever survives if React kept it. */
function StatefulProbe({ testId }: { testId: string }) {
  const [count, setCount] = useState(0);

  return (
    <div data-testid={testId}>
      <button type="button" onClick={() => setCount((value) => value + 1)}>
        {count}
      </button>
    </div>
  );
}

describe('WorkspacePanel on a desktop viewport', () => {
  it('puts the conversation in the main landmark and the workspace beside it', () => {
    setViewportWidth(DESKTOP_WIDTH);
    renderPanel();

    expect(screen.getByRole('main')).toHaveTextContent('Conversation content');
    expect(screen.getByRole('complementary', { name: 'Inventory workspace' })).toHaveTextContent(
      'Workspace content',
    );
  });

  it('shows both at once, with no tabs to navigate', () => {
    setViewportWidth(DESKTOP_WIDTH);
    renderPanel();

    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.getByText('Conversation content')).toBeVisible();
    expect(screen.getByText('Workspace content')).toBeVisible();
  });

  it('reads the conversation first, so assistive technology reaches it before the workspace', () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { container } = renderPanel();

    const text = container.textContent ?? '';
    expect(text.indexOf('Conversation content')).toBeLessThan(text.indexOf('Workspace content'));
  });
});

describe('WorkspacePanel on a narrow viewport', () => {
  it('keeps the page inside a main landmark, with the tab panel in it', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    // An explicit role replaces an element's implicit one, so putting role="tabpanel" on <main> would
    // delete the page's only main landmark at exactly the widths where skipping to content matters
    // most. The panel therefore lives inside main rather than being it.
    const main = screen.getByRole('main');
    expect(within(main).getByRole('tablist')).toBeInTheDocument();
    expect(within(main).getByRole('tabpanel')).toHaveTextContent('Conversation content');
  });

  it('offers an accessible tab list with the conversation selected', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    const tabs = screen.getAllByRole('tab');
    expect(tabs.map((tab) => tab.textContent)).toEqual(['Conversation', 'Inventory']);
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');
    expect(tabs[1]).toHaveAttribute('aria-selected', 'false');
  });

  it('hides the inactive panel from assistive technology and the tab order, without unmounting it', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    // Still mounted - so no in-flight state, stream, or focus it might hold is discarded - just
    // out of the accessibility tree and the tab order, which is what "shows only" has to mean here.
    expect(screen.getByRole('tabpanel')).toHaveTextContent('Conversation content');
    expect(screen.getByText('Workspace content')).not.toBeVisible();
  });

  it('switches panels when a tab is chosen, hiding the other without unmounting it', async () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    await userEvent.click(screen.getByRole('tab', { name: 'Inventory' }));

    expect(screen.getByRole('tabpanel')).toHaveTextContent('Workspace content');
    expect(screen.getByRole('tab', { name: 'Inventory' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('Conversation content')).not.toBeVisible();
  });

  it('moves between tabs with the arrow keys, and only the selected tab is in the tab order', async () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    const [conversationTab, inventoryTab] = screen.getAllByRole('tab');
    expect(conversationTab).toHaveAttribute('tabindex', '0');
    expect(inventoryTab).toHaveAttribute('tabindex', '-1');

    conversationTab.focus();
    await userEvent.keyboard('{ArrowRight}');

    expect(inventoryTab).toHaveAttribute('aria-selected', 'true');
    expect(inventoryTab).toHaveFocus();

    await userEvent.keyboard('{Home}');
    expect(conversationTab).toHaveAttribute('aria-selected', 'true');
    expect(conversationTab).toHaveFocus();
  });

  it('names each panel with the tab that controls it', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    const panel = screen.getByRole('tabpanel');
    const tab = screen.getByRole('tab', { name: 'Conversation' });

    expect(panel).toHaveAttribute('aria-labelledby', tab.id);
    expect(tab).toHaveAttribute('aria-controls', panel.id);
  });

  it("keeps both panels present, so neither tab's aria-controls ever points at a missing element", () => {
    setViewportWidth(NARROW_WIDTH);
    const { container } = renderPanel();

    for (const tab of screen.getAllByRole('tab')) {
      const controlledId = tab.getAttribute('aria-controls');
      expect(controlledId).toBeTruthy();
      // Queried directly on the DOM, not through an accessibility-tree query, since the point is
      // that the id exists at all - including for the inactive panel, which is hidden but present.
      expect(container.querySelector(`#${controlledId}`)).not.toBeNull();
    }
  });

  it('keeps the conversation first in the document, so it stays the primary surface', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    const tabs = screen.getAllByRole('tab');
    expect(tabs[0]).toHaveTextContent('Conversation');
  });
});

describe('WorkspacePanel across a breakpoint transition', () => {
  it('keeps the conversation and workspace mounted - not remounted - when the viewport crosses the breakpoint and back', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    render(
      <WorkspacePanel
        conversation={<StatefulProbe testId="conversation-probe" />}
        workspace={<StatefulProbe testId="workspace-probe" />}
      />,
    );

    const conversationProbe = screen.getByTestId('conversation-probe');
    const workspaceProbe = screen.getByTestId('workspace-probe');

    await userEvent.click(within(conversationProbe).getByRole('button'));
    await userEvent.click(within(conversationProbe).getByRole('button'));
    await userEvent.click(within(workspaceProbe).getByRole('button'));

    expect(conversationProbe).toHaveTextContent('2');
    expect(workspaceProbe).toHaveTextContent('1');

    act(() => {
      setViewportWidth(NARROW_WIDTH);
    });

    // The layout genuinely changed to the narrow, tabbed arrangement...
    expect(screen.getByRole('tablist')).toBeInTheDocument();
    // ...yet both probes are the very same DOM nodes, with their state intact - proof neither
    // was unmounted and remounted by the transition.
    expect(screen.getByTestId('conversation-probe')).toBe(conversationProbe);
    expect(screen.getByTestId('workspace-probe')).toBe(workspaceProbe);
    expect(conversationProbe).toHaveTextContent('2');
    expect(workspaceProbe).toHaveTextContent('1');

    act(() => {
      setViewportWidth(DESKTOP_WIDTH);
    });

    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.getByTestId('conversation-probe')).toBe(conversationProbe);
    expect(screen.getByTestId('workspace-probe')).toBe(workspaceProbe);
    expect(conversationProbe).toHaveTextContent('2');
    expect(workspaceProbe).toHaveTextContent('1');
  });

  it('unsubscribes its media query listener when unmounted', () => {
    setViewportWidth(DESKTOP_WIDTH);
    const originalMatchMedia = window.matchMedia;
    const subscriptions: { addSpy: ReturnType<typeof vi.spyOn>; removeSpy: ReturnType<typeof vi.spyOn> }[] = [];

    // useMediaQuery reads a fresh MediaQueryList on every render (for its snapshot) as well as one
    // to subscribe to, so every list this test sees is spied on, and the subscribed one - the one
    // that actually gets an `addEventListener` call - is picked out afterwards.
    window.matchMedia = (query: string) => {
      const list = originalMatchMedia(query);
      subscriptions.push({
        addSpy: vi.spyOn(list, 'addEventListener'),
        removeSpy: vi.spyOn(list, 'removeEventListener'),
      });
      return list;
    };

    const { unmount } = renderPanel();

    const subscribed = subscriptions.find(({ addSpy }) => addSpy.mock.calls.length > 0);
    expect(subscribed).toBeDefined();
    const [, listener] = subscribed!.addSpy.mock.calls[0];

    unmount();
    window.matchMedia = originalMatchMedia;

    expect(subscribed!.removeSpy).toHaveBeenCalledWith('change', listener);
  });
});
