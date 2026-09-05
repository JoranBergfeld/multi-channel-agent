import { useCallback, useSyncExternalStore } from 'react';

/**
 * Whether the viewport currently matches a CSS media query, kept up to date as it changes.
 *
 * The layout has to be a real branch and not only a stylesheet: below the breakpoint the workspace
 * is behind a tab, and a tab whose panel is merely hidden with CSS is still in the accessibility
 * tree, still focusable, and still read out. Deciding it here means the DOM says what the screen
 * shows.
 *
 * Reads through `useSyncExternalStore` rather than an effect that calls `setState`: the browser's
 * `MediaQueryList` is the external store, and this is its canonical synchronization hook - the first
 * render already reflects the current viewport with no synchronize-after-paint flash, and no lint
 * warning about calling `setState` synchronously inside an effect.
 *
 * `subscribe` and `getSnapshot` are each memoized by `query` rather than passed as fresh closures:
 * `useSyncExternalStore` re-subscribes (tears down the old listener, adds a new one) whenever
 * `subscribe`'s identity changes, so an inline closure would tear down and recreate the listener on
 * every render of whatever calls this hook - including one caused by something this hook has
 * nothing to do with, like WorkspacePanel switching its selected tab.
 */
export function useMediaQuery(query: string): boolean {
  const subscribe = useCallback(
    (onStoreChange: () => void) => {
      const list = window.matchMedia(query);
      list.addEventListener('change', onStoreChange);

      return () => list.removeEventListener('change', onStoreChange);
    },
    [query],
  );

  const getSnapshot = useCallback(() => window.matchMedia(query).matches, [query]);

  return useSyncExternalStore(subscribe, getSnapshot);
}

/**
 * The one breakpoint this application has. Below it the conversation and the workspace cannot both
 * be usable at once, so the workspace moves behind a tab.
 */
export const NARROW_SCREEN_QUERY = '(max-width: 1023px)';
