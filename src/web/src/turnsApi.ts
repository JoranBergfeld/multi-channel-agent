export interface SubmitTurnRequest {
  nativeMessageId: string;
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

export interface StockRowView {
  id: string;
  name: string;
  unit: string;
  location: string | null;
  note: string | null;
  quantity: string;
}

export interface StockListPayload {
  version: number;
  kind: 'stock_list';
  rows: StockRowView[];
  nextCursor: string | null;
  hasMore: boolean;
}

export interface StockFindPayload {
  version: number;
  kind: 'stock_find';
  candidates: StockRowView[];
  hasMoreCandidates: boolean;
}

export type TurnOutcomePayload = StockListPayload | StockFindPayload;

export interface TurnOutcomeView {
  turnId: string;
  status: string;
  /**
   * The semantic shape of the answer ('completed', 'not_found', 'ambiguous', 'invalid', ...).
   * `status` only says whether processing itself succeeded: a deterministic answer such as
   * 'not_found' is completed processing, never a failure.
   */
  category: string;
  code: string;
  summary: string;
  payload: TurnOutcomePayload | null;
  deliveries: DeliveryView[];
}

/**
 * Submits a normalized Turn to the application boundary. Participant and ChannelConversation are
 * always derived server-side from the authenticated session and the web conversation cookie - the
 * request body never carries either, so a caller cannot claim someone else's identity.
 */
export async function submitTurn(request: SubmitTurnRequest, csrfToken: string): Promise<SubmitTurnResponse> {
  const response = await fetch('/api/turns', {
    method: 'POST',
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': csrfToken,
    },
    body: JSON.stringify(request),
  });

  if (!response.ok) {
    throw new Error(`Submitting the Turn failed with status ${response.status}.`);
  }

  return (await response.json()) as SubmitTurnResponse;
}

/**
 * Reads back the recorded Outcome for a Turn. Returns null while the Turn has not yet reached a
 * terminal Outcome (the caller should poll again shortly), or if it belongs to a different
 * Participant - the same non-disclosing shape as "unknown Turn".
 */
export async function getTurnOutcome(turnId: string): Promise<TurnOutcomeView | null> {
  const response = await fetch(`/api/turns/${turnId}/outcome`, { credentials: 'include' });

  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`Reading the Outcome failed with status ${response.status}.`);
  }

  return (await response.json()) as TurnOutcomeView;
}
