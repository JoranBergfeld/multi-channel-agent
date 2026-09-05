/** What the server reports a conversation reset did. */
export interface ConversationRotationView {
  foundryConversationId: string;
  generation: number;
  /** True when something was waiting to be confirmed, so the Participant can be told it no longer is. */
  clearedPendingConfirmation: boolean;
}

/**
 * Starts a fresh conversation for this browser profile. The body is deliberately empty: which
 * Participant and which conversation are being reset is always trusted server-side context - the
 * signed-in session and this profile's own web conversation cookie - never anything the client says.
 */
export async function startNewConversation(csrfToken: string): Promise<ConversationRotationView> {
  const response = await fetch('/api/conversation/new', {
    method: 'POST',
    credentials: 'include',
    headers: { 'X-CSRF-TOKEN': csrfToken },
  });

  if (!response.ok) {
    throw new Error(`Starting a new conversation failed with status ${response.status}.`);
  }

  return (await response.json()) as ConversationRotationView;
}
