import { useCallback, useEffect, useState } from 'react';
import { grantOrChangeRole, listMembers, removeMember, transferOwnership, type MemberView, type MembershipMutationOutcome } from './governanceApi';

interface Props {
  inventoryId: string;
  csrfToken: string;
  /** Refreshes the caller's own session bootstrap - needed after a transfer changes the caller's own role. */
  onOwnershipChanged: () => void;
}

/** A short, human-readable description for every non-'ok' outcome, used in the error banner below. */
function describeFailure(outcome: Exclude<MembershipMutationOutcome, 'ok'>): string {
  switch (outcome) {
    case 'transientFailure':
      return 'a temporary server error; please try again';
    default:
      return outcome;
  }
}

/**
 * The minimal Owner-only governance panel for one Inventory: the current member roster, a form to
 * grant/change a Viewer or Editor role, a remove button per non-Owner member, and an ownership
 * transfer form. Only ever rendered when the signed-in Participant's role on this Inventory is
 * "Owner" - the endpoints themselves also enforce this, so this component is a convenience, not the
 * authorization boundary.
 */
function InventoryGovernance({ inventoryId, csrfToken, onOwnershipChanged }: Props) {
  const [members, setMembers] = useState<MemberView[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [targetIdentifier, setTargetIdentifier] = useState('');
  const [role, setRole] = useState<'Viewer' | 'Editor'>('Viewer');
  const [transferTarget, setTransferTarget] = useState('');
  const [busy, setBusy] = useState(false);

  const loadMembers = useCallback(async () => {
    const result = await listMembers(inventoryId);
    setMembers(result ?? []);
  }, [inventoryId]);

  useEffect(() => {
    void (async () => {
      await loadMembers();
    })();
  }, [loadMembers]);

  async function handleGrant(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const outcome = await grantOrChangeRole(inventoryId, targetIdentifier, role, csrfToken);
      if (outcome !== 'ok') {
        setError(`Granting failed: ${describeFailure(outcome)}.`);
        return;
      }
      setTargetIdentifier('');
      await loadMembers();
    } finally {
      setBusy(false);
    }
  }

  async function handleRemove(participantId: string) {
    setBusy(true);
    setError(null);
    try {
      const outcome = await removeMember(inventoryId, participantId, csrfToken);
      if (outcome !== 'ok') {
        setError(`Removing failed: ${describeFailure(outcome)}.`);
        return;
      }
      await loadMembers();
    } finally {
      setBusy(false);
    }
  }

  async function handleTransfer(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const outcome = await transferOwnership(inventoryId, transferTarget, csrfToken);
      if (outcome !== 'ok') {
        setError(`Transferring ownership failed: ${describeFailure(outcome)}.`);
        return;
      }
      setTransferTarget('');
      onOwnershipChanged();
    } finally {
      setBusy(false);
    }
  }

  if (members === null) {
    return null;
  }

  return (
    <section aria-label="Manage members">
      <h3>Manage members</h3>
      {error && <p role="alert">{error}</p>}

      <ul>
        {members.map((member) => (
          <li key={member.participantId}>
            {member.displayName} — {member.role}
            {member.role !== 'Owner' && (
              <button type="button" onClick={() => void handleRemove(member.participantId)} disabled={busy}>
                Remove
              </button>
            )}
          </li>
        ))}
      </ul>

      <form onSubmit={handleGrant}>
        <label htmlFor="grantTargetIdentifier">Grant/change role for (Entra object id or address)</label>
        <input
          id="grantTargetIdentifier"
          value={targetIdentifier}
          onChange={(event) => setTargetIdentifier(event.target.value)}
          required
        />
        <select value={role} onChange={(event) => setRole(event.target.value as 'Viewer' | 'Editor')}>
          <option value="Viewer">Viewer</option>
          <option value="Editor">Editor</option>
        </select>
        <button type="submit" disabled={busy || targetIdentifier.trim().length === 0}>
          Grant / change role
        </button>
      </form>

      <form onSubmit={handleTransfer}>
        <label htmlFor="transferTargetIdentifier">Transfer ownership to (Entra object id or address)</label>
        <input
          id="transferTargetIdentifier"
          value={transferTarget}
          onChange={(event) => setTransferTarget(event.target.value)}
          required
        />
        <button type="submit" disabled={busy || transferTarget.trim().length === 0}>
          Transfer ownership
        </button>
      </form>
    </section>
  );
}

export default InventoryGovernance;
