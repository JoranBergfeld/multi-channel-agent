/** One member of an Inventory, as shown only to its Owner. */
export interface MemberView {
  participantId: string;
  displayName: string;
  role: string;
}

export type MembershipMutationOutcome = 'ok' | 'notFound' | 'forbidden' | 'invalid' | 'conflict';

function outcomeForStatus(status: number): MembershipMutationOutcome {
  if (status === 404) return 'notFound';
  if (status === 403) return 'forbidden';
  if (status === 400) return 'invalid';
  if (status === 409) return 'conflict';
  return 'ok';
}

/** Owner-only: lists the current membership roster for one Inventory. Never reachable by a non-Owner. */
export async function listMembers(inventoryId: string): Promise<MemberView[] | null> {
  const response = await fetch(`/api/inventories/${inventoryId}/members`, { credentials: 'include' });

  if (response.status === 404 || response.status === 403) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`Listing members failed with status ${response.status}.`);
  }

  return (await response.json()) as MemberView[];
}

/**
 * Owner-only: grants a new Viewer/Editor Membership (identified by Entra object id or verified
 * tenant address) or changes an existing one's role. Recipient acceptance is never required.
 */
export async function grantOrChangeRole(
  inventoryId: string,
  targetIdentifier: string,
  role: 'Viewer' | 'Editor',
  csrfToken: string,
): Promise<MembershipMutationOutcome> {
  const response = await fetch(`/api/inventories/${inventoryId}/members`, {
    method: 'PUT',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
    body: JSON.stringify({ targetIdentifier, role }),
  });

  return outcomeForStatus(response.status);
}

/** Owner-only: removes a non-Owner member. The current Owner can never be removed through this path. */
export async function removeMember(inventoryId: string, participantId: string, csrfToken: string): Promise<MembershipMutationOutcome> {
  const response = await fetch(`/api/inventories/${inventoryId}/members/${participantId}`, {
    method: 'DELETE',
    credentials: 'include',
    headers: { 'X-CSRF-TOKEN': csrfToken },
  });

  return outcomeForStatus(response.status);
}

/**
 * Owner-only: atomically transfers ownership to an existing, resolvable, active tenant member. The
 * previous Owner is demoted to Editor, preserving their access. Self-transfer is rejected as a
 * conflict.
 */
export async function transferOwnership(inventoryId: string, targetIdentifier: string, csrfToken: string): Promise<MembershipMutationOutcome> {
  const response = await fetch(`/api/inventories/${inventoryId}/transfer-ownership`, {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
    body: JSON.stringify({ targetIdentifier }),
  });

  return outcomeForStatus(response.status);
}
