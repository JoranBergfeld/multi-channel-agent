import { describe, expect, it, vi } from 'vitest'

import { recordingEventStreamFactory } from './testing/fakeEventSource'
import { openInventoryStream } from './inventoryStream'
import type { InventoryVersions } from './inventoryStream'

describe('openInventoryStream', () => {
  it('opens the Participant-level stream', () => {
    const { opened, factory } = recordingEventStreamFactory()

    openInventoryStream({ onVersions: () => {}, createSource: factory })

    expect(opened).toHaveLength(1)
    expect(opened[0]?.url).toBe('/api/inventory-events')
  })

  it('reports the whole snapshot the moment it connects', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onVersions = vi.fn()

    openInventoryStream({ onVersions, createSource: factory })
    opened[0]!.emit('snapshot', {
      inventories: [
        { inventoryId: 'inventory-1', version: 3 },
        { inventoryId: 'inventory-2', version: 0 },
      ],
    })

    expect(onVersions).toHaveBeenCalledWith({ 'inventory-1': 3, 'inventory-2': 0 })
  })

  it('does not let a caller corrupt internal state by mutating the object handed to onVersions', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onVersions = vi.fn()

    openInventoryStream({ onVersions, createSource: factory })
    opened[0]!.emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 3 }] })

    // Reaches into the very object the stream handed back and pollutes it - exactly what a fresh
    // object literal on every publish is meant to make harmless.
    const firstReceived = onVersions.mock.calls[0][0] as InventoryVersions
    firstReceived['inventory-1'] = 999
    firstReceived['inventory-2'] = 42

    opened[0]!.emit('changed', { inventoryId: 'inventory-1', version: 4 })

    // Had the mutation reached the internal picture, this would also carry the stray
    // "inventory-2": 42 the caller injected above.
    expect(onVersions).toHaveBeenLastCalledWith({ 'inventory-1': 4 })
  })

  it('folds each later change into the picture it already had', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onVersions = vi.fn()

    openInventoryStream({ onVersions, createSource: factory })
    opened[0]!.emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 3 }] })
    opened[0]!.emit('changed', { inventoryId: 'inventory-1', version: 4 })

    expect(onVersions).toHaveBeenLastCalledWith({ 'inventory-1': 4 })
  })

  it('drops an Inventory the Participant may no longer see', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onVersions = vi.fn()

    openInventoryStream({ onVersions, createSource: factory })
    opened[0]!.emit('snapshot', {
      inventories: [
        { inventoryId: 'inventory-1', version: 3 },
        { inventoryId: 'inventory-2', version: 1 },
      ],
    })
    opened[0]!.emit('revoked', { inventoryId: 'inventory-2' })

    expect(onVersions).toHaveBeenLastCalledWith({ 'inventory-1': 3 })
  })

  it('replaces the whole picture on the next snapshot, so a reconnect is a total resynchronization', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onVersions = vi.fn()

    openInventoryStream({ onVersions, createSource: factory })
    opened[0]!.emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 3 }] })
    opened[0]!.emit('snapshot', { inventories: [{ inventoryId: 'inventory-2', version: 9 }] })

    expect(onVersions).toHaveBeenLastCalledWith({ 'inventory-2': 9 })
  })

  it('stops reporting once the caller closes it', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onVersions = vi.fn()

    const stream = openInventoryStream({ onVersions, createSource: factory })
    stream.close()
    opened[0]!.emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 3 }] })

    expect(onVersions).not.toHaveBeenCalled()
    expect(opened[0]!.closed).toBe(true)
  })

  it('reports a permanent connection failure through onFailed exactly once and closes the source', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onFailed = vi.fn()

    openInventoryStream({ onVersions: () => {}, onFailed, createSource: factory })
    opened[0]!.failFatally()

    expect(onFailed).toHaveBeenCalledTimes(1)
    expect(opened[0]!.closed).toBe(true)
  })

  it('ignores a transient connection failure, leaving the source open for the browser to reconnect on its own', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onFailed = vi.fn()

    openInventoryStream({ onVersions: () => {}, onFailed, createSource: factory })
    opened[0]!.fail()

    expect(onFailed).not.toHaveBeenCalled()
    expect(opened[0]!.closed).toBe(false)
  })
})
