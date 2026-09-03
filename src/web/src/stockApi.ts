import type { StockRowView } from './turnsApi';

export interface StockListView {
  rows: StockRowView[];
  nextCursor: string | null;
  hasMore: boolean;
}

/**
 * Fetches the authoritative on-hand Stock projection for one Inventory - the same authorized read
 * the conversational list_stock tool call uses. Returns null when the Inventory is not authorized
 * for the current Participant (or does not exist - the two are indistinguishable by design).
 */
export async function fetchStock(inventoryId: string): Promise<StockListView | null> {
  const response = await fetch(`/api/inventories/${inventoryId}/stock`, { credentials: 'include' });

  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`Reading the Stock projection failed with status ${response.status}.`);
  }

  return (await response.json()) as StockListView;
}
