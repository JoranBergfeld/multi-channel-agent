import { describe, expect, it } from 'vitest'

import { DESKTOP_WIDTH, NARROW_WIDTH, setViewportWidth } from './setup'

describe('test runtime setup', () => {
  it('resolves max-width and min-width media queries against the width set by setViewportWidth', () => {
    // `beforeEach` in setup.ts already put us at DESKTOP_WIDTH.
    expect(window.matchMedia(`(max-width: ${NARROW_WIDTH}px)`).matches).toBe(false)
    expect(window.matchMedia(`(min-width: ${DESKTOP_WIDTH}px)`).matches).toBe(true)

    setViewportWidth(NARROW_WIDTH)

    expect(window.matchMedia(`(max-width: ${NARROW_WIDTH}px)`).matches).toBe(true)
    expect(window.matchMedia(`(min-width: ${DESKTOP_WIDTH}px)`).matches).toBe(false)

    // Leave something behind for the next test to prove does not leak across tests.
    window.localStorage.setItem('leftover', 'from-a-previous-test')
  })

  it('starts with empty localStorage even though the previous test wrote to it', () => {
    expect(window.localStorage.length).toBe(0)
    expect(window.localStorage.getItem('leftover')).toBeNull()
  })
})
