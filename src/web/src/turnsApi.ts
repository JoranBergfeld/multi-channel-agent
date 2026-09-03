export interface SubmitTurnRequest {
  nativeMessageId: string;
  channelConversationId: string;
  contentText: string;
  locale?: string;
  traceId?: string;
}

export interface SubmitTurnResponse {
  turnId: string;
  alreadyAccepted: boolean;
}

export interface DeliveryView {
  deliveryId: string;
  channel: string;
  status: string;
  attempts: number;
}

export interface TurnOutcomeView {
  turnId: string;
  status: string;
  code: string;
  summary: string;
  deliveries: DeliveryView[];
}

/** Submits a normalized synthetic Turn to the application boundary. */
export async function submitTurn(request: SubmitTurnRequest): Promise<SubmitTurnResponse> {
  const response = await fetch('/api/turns', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });

  if (!response.ok) {
    throw new Error(`Submitting the Turn failed with status ${response.status}.`);
  }

  return (await response.json()) as SubmitTurnResponse;
}

/**
 * Reads back the recorded Outcome for a Turn. Returns null while the Turn has not yet reached a
 * terminal Outcome (the caller should poll again shortly).
 */
export async function getTurnOutcome(turnId: string): Promise<TurnOutcomeView | null> {
  const response = await fetch(`/api/turns/${turnId}/outcome`);

  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`Reading the Outcome failed with status ${response.status}.`);
  }

  return (await response.json()) as TurnOutcomeView;
}
