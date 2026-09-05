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
 * Renders one tree shape at every width - a `main` landmark that always wraps the same three
 * children, in the same order: the tab list, the conversation panel, and the Inventory panel. Only
 * their attributes and visibility respond to the viewport, never their presence, so `conversation`
 * and `workspace` are never unmounted (and never lose their state, in-flight streams, or focus) by a
 * breakpoint change. On a wide viewport the tab list is inert and the Inventory panel is a
 * `complementary` landmark beside the conversation; below the breakpoint the Inventory panel becomes
 * an ARIA tabpanel and the tab list switches between them. Either way the inactive panel - if any -
 * is taken out of the accessibility tree and the tab order with the semantic `hidden` attribute
 * instead of being removed, so both panel ids always exist for the tabs' `aria-controls` to point at.
 *
 * The conversation panel is always first in document order, and its tab is selected by default, which
 * is what actually keeps it primary - CSS placement doesn't decide reading or default focus order.
 */
function WorkspacePanel({ conversation, workspace }: WorkspacePanelProps) {
  const isNarrow = useMediaQuery(NARROW_SCREEN_QUERY);
  const [selected, setSelected] = useState<TabId>('conversation');
  const tabRefs = useRef<Record<TabId, HTMLButtonElement | null>>({ conversation: null, workspace: null });
  const [wasNarrow, setWasNarrow] = useState(isNarrow);

  /*
   * Corrected synchronously during render - before this transition ever reaches the DOM - because
   * once a focused element's container is actually hidden, the browser may already have moved focus
   * to <body> by the time any effect could run, and there is no reclaiming it after the fact.
   * Comparing against `wasNarrow` (the *previous* render's viewport, not `selected` state) is what
   * tells a genuine desktop-to-narrow transition apart from an ordinary rerender while already
   * narrow, where `selected` alone has to keep governing - otherwise every rerender would re-detect
   * "just narrowed" and a deliberate tab click could never stick.
   *
   * This is the "adjust state while rendering" pattern React's own docs describe for exactly this
   * "compare against the last render" shape, using state - never a ref's `current` - so the read is
   * never stale: React discards this render's output and immediately retries with both `wasNarrow`
   * and `selected` already corrected, so every read of either below already sees the final value.
   * Looking the panels up by id rather than through a ref to their element keeps the focus check
   * itself a plain DOM read too - like `useMediaQuery` reading `matchMedia(...).matches` - rather
   * than a ref access.
   */
  if (isNarrow !== wasNarrow) {
    setWasNarrow(isNarrow);

    if (isNarrow) {
      const focusedPanel = TABS.map((tab) => tab.id).find((id) =>
        document.getElementById(`workspace-panel-${id}`)?.contains(document.activeElement),
      );

      if (focusedPanel && focusedPanel !== selected) {
        setSelected(focusedPanel);
      }
    }
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
    <main className={isNarrow ? 'workspace-layout workspace-layout-narrow' : 'workspace-layout'}>
      {/*
        Always rendered at this same position so its (dis)appearance from an assistive-technology
        or visual point of view never shifts the panels below it - only its role and content are
        conditional on the viewport, never its presence in the tree.
      */}
      <div
        role={isNarrow ? 'tablist' : undefined}
        aria-label={isNarrow ? 'Workspace sections' : undefined}
        hidden={!isNarrow}
        className="workspace-tabs"
      >
        {isNarrow &&
          TABS.map((tab) => (
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
        id="workspace-panel-conversation"
        className="workspace-conversation"
        role={isNarrow ? 'tabpanel' : undefined}
        aria-labelledby={isNarrow ? 'workspace-tab-conversation' : undefined}
        hidden={isNarrow && selected !== 'conversation'}
        tabIndex={isNarrow ? 0 : undefined}
      >
        {conversation}
      </div>

      <aside
        id="workspace-panel-workspace"
        className="workspace-panel"
        aria-label={isNarrow ? undefined : 'Inventory workspace'}
        role={isNarrow ? 'tabpanel' : undefined}
        aria-labelledby={isNarrow ? 'workspace-tab-workspace' : undefined}
        hidden={isNarrow && selected !== 'workspace'}
        tabIndex={isNarrow ? 0 : undefined}
      >
        {workspace}
      </aside>
    </main>
  );
}

export default WorkspacePanel;
