export interface InventoryView {
  id: string;
  shortId: string;
  name: string;
  ownerDisplayName: string;
  role: string;
}

export interface BootstrapView {
  participantId: string;
  displayName: string;
  webConversationId: string;
  inventories: InventoryView[];
  activeInventoryId: string | null;
  needsOnboarding: boolean;
}

export interface BootstrapResponse {
  bootstrap: BootstrapView;
  csrfToken: string;
}

export type BootstrapResult =
  | { status: 'ok'; data: BootstrapResponse }
  | { status: 'unauthenticated' }
  | { status: 'forbidden' };

/**
 * Reads the authenticated session bootstrap: the canonical Participant, their authorized
 * Inventories, the current Active Inventory (if any), whether they need onboarding, and a fresh
 * CSRF token for subsequent mutating calls. Distinguishes "not signed in" from "signed in but not
 * an active tenant member" only by status code - never by response body detail - matching the
 * BFF's non-disclosing refusal contract.
 */
export async function fetchBootstrap(): Promise<BootstrapResult> {
  const response = await fetch('/api/session/bootstrap', { credentials: 'include' });

  if (response.status === 401) {
    return { status: 'unauthenticated' };
  }

  if (response.status === 403) {
    return { status: 'forbidden' };
  }

  if (!response.ok) {
    throw new Error(`Reading the session bootstrap failed with status ${response.status}.`);
  }

  return { status: 'ok', data: (await response.json()) as BootstrapResponse };
}

/** Explicitly creates a named Inventory. Idempotent by clientRequestId: retrying the same call is safe. */
export async function createInventory(name: string, clientRequestId: string, csrfToken: string): Promise<InventoryView> {
  const response = await fetch('/api/inventories', {
    method: 'POST',
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': csrfToken,
    },
    body: JSON.stringify({ name, clientRequestId }),
  });

  if (!response.ok) {
    throw new Error(`Creating the Inventory failed with status ${response.status}.`);
  }

  return (await response.json()) as InventoryView;
}

/** Lists only the Inventories the current Participant is authorized for. */
export async function listInventories(): Promise<InventoryView[]> {
  const response = await fetch('/api/inventories', { credentials: 'include' });

  if (!response.ok) {
    throw new Error(`Listing Inventories failed with status ${response.status}.`);
  }

  return (await response.json()) as InventoryView[];
}

/**
 * Explicitly switches the Active Inventory for the current web conversation. A 404 means the
 * Inventory either does not exist or is not authorized for this Participant - the two are
 * indistinguishable by design.
 */
export async function selectInventory(inventoryId: string, csrfToken: string): Promise<boolean> {
  const response = await fetch(`/api/inventories/${inventoryId}/select`, {
    method: 'POST',
    credentials: 'include',
    headers: { 'X-CSRF-TOKEN': csrfToken },
  });

  if (response.status === 404) {
    return false;
  }

  if (!response.ok) {
    throw new Error(`Selecting the Inventory failed with status ${response.status}.`);
  }

  return true;
}
