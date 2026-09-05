import { describe, expect, it } from 'vitest';
import { render, screen, within } from '@testing-library/react';
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

  it('shows only the selected panel', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    expect(screen.getByRole('tabpanel')).toHaveTextContent('Conversation content');
    expect(screen.queryByText('Workspace content')).not.toBeInTheDocument();
  });

  it('switches panels when a tab is chosen', async () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    await userEvent.click(screen.getByRole('tab', { name: 'Inventory' }));

    expect(screen.getByRole('tabpanel')).toHaveTextContent('Workspace content');
    expect(screen.getByRole('tab', { name: 'Inventory' })).toHaveAttribute('aria-selected', 'true');
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

  it('keeps the conversation first in the document, so it stays the primary surface', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    const tabs = screen.getAllByRole('tab');
    expect(tabs[0]).toHaveTextContent('Conversation');
  });
});
