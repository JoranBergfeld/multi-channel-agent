import { useEffect, useState } from 'react';

/**
 * Whether the viewport currently matches a CSS media query, kept up to date as it changes.
 *
 * The layout has to be a real branch and not only a stylesheet: below the breakpoint the workspace
 * is behind a tab, and a tab whose panel is merely hidden with CSS is still in the accessibility
 * tree, still focusable, and still read out. Deciding it here means the DOM says what the screen
 * shows.
 */
export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches);

  useEffect(() => {
    const list = window.matchMedia(query);
    const onChange = (event: MediaQueryListEvent) => setMatches(event.matches);

    setMatches(list.matches);
    list.addEventListener('change', onChange);

    return () => list.removeEventListener('change', onChange);
  }, [query]);

  return matches;
}

/**
 * The one breakpoint this application has. Below it the conversation and the workspace cannot both
 * be usable at once, so the workspace moves behind a tab.
 */
export const NARROW_SCREEN_QUERY = '(max-width: 1023px)';
