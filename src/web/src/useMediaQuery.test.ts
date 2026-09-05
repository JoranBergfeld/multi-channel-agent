import { describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { DESKTOP_WIDTH, setViewportWidth } from './testing/setup';
import { useMediaQuery } from './useMediaQuery';

/**
 * Spies on every `MediaQueryList` `window.matchMedia` creates for the lifetime of this call, so
 * `addEventListener`/`removeEventListener` calls on each one can be inspected individually - some
 * lists are only ever read (`useSyncExternalStore`'s snapshot) and never subscribed to at all.
 */
function spyOnMatchMedia() {
  const originalMatchMedia = window.matchMedia;
  const lists: { list: MediaQueryList; addSpy: ReturnType<typeof vi.spyOn>; removeSpy: ReturnType<typeof vi.spyOn> }[] =
    [];

  window.matchMedia = (query: string) => {
    const list = originalMatchMedia(query);
    lists.push({
      list,
      addSpy: vi.spyOn(list, 'addEventListener'),
      removeSpy: vi.spyOn(list, 'removeEventListener'),
    });
    return list;
  };

  return {
    lists,
    subscribed: () => lists.find(({ addSpy }) => addSpy.mock.calls.length > 0),
    restore: () => {
      window.matchMedia = originalMatchMedia;
    },
  };
}

describe('useMediaQuery', () => {
  it('does not tear down and recreate its subscription on an unrelated rerender', () => {
    setViewportWidth(DESKTOP_WIDTH);
    const spy = spyOnMatchMedia();

    const { rerender, unmount } = renderHook(({ query }) => useMediaQuery(query), {
      initialProps: { query: '(max-width: 1023px)' },
    });

    const subscribed = spy.subscribed();
    expect(subscribed).toBeDefined();
    expect(subscribed!.addSpy).toHaveBeenCalledTimes(1);

    // Rerendering with the very same query string is exactly what an unrelated state change inside
    // a consumer (WorkspacePanel switching its selected tab, say) looks like from this hook's side -
    // a stable subscribe/getSnapshot pair must not tear down and recreate the listener for it.
    rerender({ query: '(max-width: 1023px)' });
    rerender({ query: '(max-width: 1023px)' });

    expect(subscribed!.addSpy).toHaveBeenCalledTimes(1);
    expect(subscribed!.removeSpy).not.toHaveBeenCalled();

    unmount();
    expect(subscribed!.removeSpy).toHaveBeenCalledTimes(1);
    spy.restore();
  });

  it('tears down the old subscription and starts a new one when the query changes', () => {
    setViewportWidth(DESKTOP_WIDTH);
    const spy = spyOnMatchMedia();

    const { result, rerender, unmount } = renderHook(({ query }) => useMediaQuery(query), {
      initialProps: { query: '(max-width: 1023px)' },
    });

    // 1280px (DESKTOP_WIDTH) doesn't match max-width:1023px.
    expect(result.current).toBe(false);
    const first = spy.subscribed();
    expect(first).toBeDefined();

    rerender({ query: '(min-width: 100px)' });

    // The old listener is gone, a new one exists for the new query, and the returned value now
    // reflects that query instead of the stale one.
    expect(first!.removeSpy).toHaveBeenCalledTimes(1);
    const second = spy.lists.find(
      (entry) => entry !== first && entry.list.media === '(min-width: 100px)' && entry.addSpy.mock.calls.length > 0,
    );
    expect(second).toBeDefined();
    expect(second!.addSpy).toHaveBeenCalledTimes(1);
    // 1280px matches min-width:100px.
    expect(result.current).toBe(true);

    unmount();
    expect(second!.removeSpy).toHaveBeenCalledTimes(1);
    spy.restore();
  });
});
