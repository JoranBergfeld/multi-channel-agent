import { useRef, useState } from 'react';
import { NARROW_SCREEN_QUERY, useMediaQuery } from './useMediaQuery';

interface WorkspacePanelProps {
  conversation: React.ReactNode;
  workspace: React.ReactNode;
}

const TABS = [
  { id: 'conversation', label: 'Conversation' },
  { id: 'workspace', label: 'Inventory' },
] as const;

type TabId = (typeof TABS)[number]['id'];

/**
 * The responsive frame: conversation primary, Inventory workspace beside it or behind a tab.
 *
 * On a wide viewport the conversation is the page's `main` landmark and the workspace is a
 * `complementary` one beside it, which is what "conversation primary with a live workspace" means to
 * a screen reader as much as to an eye. Below the breakpoint they cannot both be usable at once, so
 * the workspace moves behind an ARIA tab list *inside* that same `main` landmark - the landmark never
 * disappears - and only the selected panel is rendered at all, so a hidden panel is never quietly
 * focusable or read out.
 *
 * The conversation comes first in document order at every width, and its tab is selected by default.
 * Document order is what decides reading order and default focus order, so this - not CSS placement -
 * is what actually makes the conversation primary.
 */
function WorkspacePanel({ conversation, workspace }: WorkspacePanelProps) {
  const isNarrow = useMediaQuery(NARROW_SCREEN_QUERY);
  const [selected, setSelected] = useState<TabId>('conversation');
  const tabRefs = useRef<Record<TabId, HTMLButtonElement | null>>({ conversation: null, workspace: null });

  if (!isNarrow) {
    return (
      <div className="workspace-layout">
        <main className="workspace-conversation">{conversation}</main>
        <aside className="workspace-panel" aria-label="Inventory workspace">
          {workspace}
        </aside>
      </div>
    );
  }

  const select = (id: TabId) => {
    setSelected(id);
    tabRefs.current[id]?.focus();
  };

  const onKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    const index = TABS.findIndex((tab) => tab.id === selected);

    if (event.key === 'ArrowRight') {
      event.preventDefault();
      select(TABS[(index + 1) % TABS.length].id);
    } else if (event.key === 'ArrowLeft') {
      event.preventDefault();
      select(TABS[(index - 1 + TABS.length) % TABS.length].id);
    } else if (event.key === 'Home') {
      event.preventDefault();
      select(TABS[0].id);
    } else if (event.key === 'End') {
      event.preventDefault();
      select(TABS[TABS.length - 1].id);
    }
  };

  return (
    <div className="workspace-layout workspace-layout-narrow">
      {/*
        The main landmark survives the narrow layout. Putting role="tabpanel" on <main> would replace
        its implicit role and leave the page with no main at all - precisely when a screen-reader user
        skipping to content needs one most - so the tab list and the one rendered panel live inside it.
      */}
      <main className="workspace-conversation">
        <div role="tablist" aria-label="Workspace sections" className="workspace-tabs">
          {TABS.map((tab) => (
            <button
              key={tab.id}
              id={`workspace-tab-${tab.id}`}
              ref={(element) => {
                tabRefs.current[tab.id] = element;
              }}
              type="button"
              role="tab"
              aria-selected={selected === tab.id}
              aria-controls={`workspace-panel-${tab.id}`}
              // Roving tab order: the tab list is one stop, and the arrow keys move within it.
              tabIndex={selected === tab.id ? 0 : -1}
              onClick={() => select(tab.id)}
              onKeyDown={onKeyDown}
            >
              {tab.label}
            </button>
          ))}
        </div>

        <div
          id={`workspace-panel-${selected}`}
          role="tabpanel"
          aria-labelledby={`workspace-tab-${selected}`}
          tabIndex={0}
        >
          {selected === 'conversation' ? conversation : workspace}
        </div>
      </main>
    </div>
  );
}

export default WorkspacePanel;
