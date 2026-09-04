import type { LocationView, UnitView } from './turnsApi';

export interface UnitListView {
  units: UnitView[];
  nextCursor: string | null;
  hasMore: boolean;
}

export interface LocationListView {
  locations: LocationView[];
  nextCursor: string | null;
  hasMore: boolean;
}

/**
 * Fetches the authoritative active Unit catalog for one Inventory - the same authorized read the
 * conversational list_units tool call uses. Returns null when the Inventory is not authorized for
 * the current Participant (or does not exist - the two are indistinguishable by design).
 */
export async function fetchUnits(inventoryId: string): Promise<UnitListView | null> {
  return fetchCatalog<UnitListView>(`/api/inventories/${inventoryId}/units`, 'Unit');
}

/** Fetches the authoritative active Location catalog. See {@link fetchUnits}. */
export async function fetchLocations(inventoryId: string): Promise<LocationListView | null> {
  return fetchCatalog<LocationListView>(`/api/inventories/${inventoryId}/locations`, 'Location');
}

async function fetchCatalog<T>(url: string, noun: string): Promise<T | null> {
  const response = await fetch(url, { credentials: 'include' });

  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`Reading the ${noun} projection failed with status ${response.status}.`);
  }

  return (await response.json()) as T;
}
