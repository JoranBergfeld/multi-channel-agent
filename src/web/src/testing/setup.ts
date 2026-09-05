// Global Vitest setup, loaded once per test file via `vitest/config`'s `test.setupFiles`. Wires up
// jest-dom's matchers, isolates every test's DOM/localStorage/viewport from the ones around it,
// and fills in a couple of jsdom gaps (`matchMedia`, `crypto.randomUUID`) that our components rely
// on but jsdom does not implement (or only implements in newer versions than we can rely on).
import '@testing-library/jest-dom/vitest'

import { cleanup } from '@testing-library/react'
import { afterEach, beforeEach } from 'vitest'

/** A viewport width comfortably inside a desktop/pointer layout. */
export const DESKTOP_WIDTH = 1280

/** A viewport width comfortably inside a narrow/mobile layout. */
export const NARROW_WIDTH = 480

const MAX_WIDTH_QUERY = /\(max-width:\s*(\d+(?:\.\d+)?)px\)/
const MIN_WIDTH_QUERY = /\(min-width:\s*(\d+(?:\.\d+)?)px\)/

let currentWidth = DESKTOP_WIDTH

function matchesWidth(query: string, width: number): boolean {
  const maxWidth = query.match(MAX_WIDTH_QUERY)
  const minWidth = query.match(MIN_WIDTH_QUERY)

  // Fail closed: a query using neither supported token is unsupported, not a match.
  if (!maxWidth && !minWidth) {
    return false
  }

  if (maxWidth && width > Number(maxWidth[1])) {
    return false
  }

  if (minWidth && width < Number(minWidth[1])) {
    return false
  }

  return true
}

type ChangeListener = (event: MediaQueryListEvent) => void

/**
 * A structurally-typed `MediaQueryList`: it satisfies the DOM interface our components call
 * (`matches`, `addEventListener`/`removeEventListener`, the deprecated `addListener`/
 * `removeListener` pair some libraries still use, and `dispatchEvent`) without depending on
 * jsdom's own CSS engine, which does not evaluate media queries at all.
 */
class FakeMediaQueryList implements MediaQueryList {
  readonly media: string
  matches: boolean
  onchange: ((this: MediaQueryList, event: MediaQueryListEvent) => void) | null = null

  private readonly listeners = new Set<ChangeListener>()

  constructor(media: string) {
    this.media = media
    this.matches = matchesWidth(media, currentWidth)
  }

  addEventListener(type: string, listener: EventListenerOrEventListenerObject): void {
    if (type === 'change') {
      this.listeners.add(listener as ChangeListener)
    }
  }

  removeEventListener(type: string, listener: EventListenerOrEventListenerObject): void {
    if (type === 'change') {
      this.listeners.delete(listener as ChangeListener)
    }
  }

  addListener(listener: ChangeListener | null): void {
    if (listener) {
      this.listeners.add(listener)
    }
  }

  removeListener(listener: ChangeListener | null): void {
    if (listener) {
      this.listeners.delete(listener)
    }
  }

  dispatchEvent(event: Event): boolean {
    const changeEvent = event as MediaQueryListEvent
    this.onchange?.call(this, changeEvent)
    this.listeners.forEach((listener) => listener(changeEvent))
    return true
  }

  /** Re-evaluates `matches` against the new width and notifies listeners if it changed. */
  refresh(width: number): void {
    const matches = matchesWidth(this.media, width)

    if (matches === this.matches) {
      return
    }

    this.matches = matches
    this.dispatchEvent({ matches, media: this.media } as MediaQueryListEvent)
  }
}

const liveQueries = new Set<FakeMediaQueryList>()

function fakeMatchMedia(query: string): MediaQueryList {
  const mediaQueryList = new FakeMediaQueryList(query)
  liveQueries.add(mediaQueryList)
  return mediaQueryList
}

/**
 * Sets the viewport width every subsequent `window.matchMedia` max-width/min-width query is
 * evaluated against, and re-evaluates every already-created `MediaQueryList` so components
 * listening for viewport changes (via `addEventListener('change', ...)`) see one too.
 */
export function setViewportWidth(width: number): void {
  currentWidth = width
  window.innerWidth = width
  liveQueries.forEach((mediaQueryList) => mediaQueryList.refresh(width))
}

let deterministicUuidCount = 0

function deterministicRandomUUID(): `${string}-${string}-${string}-${string}-${string}` {
  deterministicUuidCount += 1
  const suffix = deterministicUuidCount.toString(16).padStart(12, '0')
  return `00000000-0000-4000-8000-${suffix}`
}

beforeEach(() => {
  liveQueries.clear()
  window.matchMedia = fakeMatchMedia
  setViewportWidth(DESKTOP_WIDTH)

  window.localStorage.clear()

  if (typeof window.crypto === 'undefined' || typeof window.crypto.randomUUID !== 'function') {
    deterministicUuidCount = 0
    window.crypto ??= {} as Crypto
    window.crypto.randomUUID = deterministicRandomUUID
  }
})

afterEach(() => {
  cleanup()
})
