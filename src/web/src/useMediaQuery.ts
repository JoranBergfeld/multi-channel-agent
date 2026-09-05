import { useSyncExternalStore } from 'react';

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
 */
export function useMediaQuery(query: string): boolean {
  return useSyncExternalStore(
    (onStoreChange) => {
      const list = window.matchMedia(query);
      list.addEventListener('change', onStoreChange);

      return () => list.removeEventListener('change', onStoreChange);
    },
    () => window.matchMedia(query).matches,
  );
}

/**
 * The one breakpoint this application has. Below it the conversation and the workspace cannot both
 * be usable at once, so the workspace moves behind a tab.
 */
export const NARROW_SCREEN_QUERY = '(max-width: 1023px)';
