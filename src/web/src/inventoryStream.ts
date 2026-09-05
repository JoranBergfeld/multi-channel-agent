import type { EventStreamFactory } from './turnStream'

/**
 * The numeric value of the browser's `EventSource.readyState` once a connection has permanently
 * failed. Written as a literal rather than read off the global `EventSource` constructor so this
 * module never depends on that global being defined - a fake source in tests supplies the same
 * number without needing to exist as a real constructor.
 */
const EVENT_SOURCE_CLOSED = 2

/** The version each Inventory this Participant may see is currently at, keyed by Inventory id. */
export type InventoryVersions = Record<string, number>

interface InventoryVersionWire {
  inventoryId: string
  version: number
}

interface InventorySnapshotWire {
  inventories: InventoryVersionWire[]
}

interface InventoryRevokedWire {
  inventoryId: string
}

export interface OpenInventoryStreamOptions {
  /** Called with the complete current picture every time any part of it changes. */
  onVersions: (versions: InventoryVersions) => void
  /** Called once the connection has failed permanently and this stream is over. */
  onFailed?: () => void
  /** Defaults to the real `EventSource`; tests supply a fake instead. */
  createSource?: EventStreamFactory
}

export interface InventoryStream {
  close: () => void
}

/**
 * Watches this Participant's Inventory invalidation stream: which Inventories they may see, and what
 * version each is at, changed by anyone through any channel.
 *
 * The server sends a complete snapshot the moment a connection opens and only differences after
 * that, so this client never needs a resume point - and correspondingly the server issues no event
 * identities for it. A reconnect is therefore a total resynchronization, which is stronger than
 * replaying a cursor would be: nothing can be missed while a tab is closed, and a Membership
 * granted or revoked in the meantime simply arrives in the next snapshot.
 *
 * Every callback hands back the whole picture rather than the delta, because that is what a caller
 * actually renders from - and because folding deltas is exactly the sort of bookkeeping a component
 * should never have to get right. The picture is stored internally as a plain `Record` keyed by
 * Inventory id (a server-issued GUID in practice), and every publish clones it with a fresh object
 * literal - never `Object.assign` onto or otherwise mutating the internal copy - so a caller can
 * never corrupt this stream's state through the reference it was handed.
 */
export function openInventoryStream({
  onVersions,
  onFailed,
  createSource = (url: string) => new EventSource(url),
}: OpenInventoryStreamOptions): InventoryStream {
  const source = createSource('/api/inventory-events')
  let versions: InventoryVersions = {}
  let closed = false

  source.onerror = () => {
    if (closed) {
      return
    }

    if (source.readyState !== EVENT_SOURCE_CLOSED) {
      // Transient: the browser reconnects on its own, and the next snapshot resynchronizes -
      // closing here, or reporting an error, would defeat that reconnect for no reason.
      return
    }

    // The browser has already given up permanently (a 401/403/404 response, or one that isn't
    // `text/event-stream`) and will not reconnect on its own. Closing here is idempotent - the
    // browser put `readyState` in this state already - but guarantees the caller-visible `closed`
    // flag agrees with reality regardless.
    closed = true
    source.close()
    onFailed?.()
  }

  function publish(): void {
    onVersions({ ...versions })
  }

  source.addEventListener('snapshot', (event) => {
    if (closed) {
      return
    }

    const snapshot = JSON.parse(event.data) as InventorySnapshotWire

    // Replaced, never merged: a snapshot is the whole truth, so an Inventory missing from it is
    // one this Participant may no longer see.
    versions = Object.fromEntries(snapshot.inventories.map((i) => [i.inventoryId, i.version]))
    publish()
  })

  source.addEventListener('changed', (event) => {
    if (closed) {
      return
    }

    const changed = JSON.parse(event.data) as InventoryVersionWire
    versions = { ...versions, [changed.inventoryId]: changed.version }
    publish()
  })

  source.addEventListener('revoked', (event) => {
    if (closed) {
      return
    }

    const revoked = JSON.parse(event.data) as InventoryRevokedWire
    const remaining: InventoryVersions = { ...versions }
    delete remaining[revoked.inventoryId]
    versions = remaining
    publish()
  })

  return {
    close: () => {
      closed = true
      source.close()
    },
  }
}
