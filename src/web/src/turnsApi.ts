export interface SubmitTurnRequest {
  nativeMessageId: string;
  contentText: string;
  locale?: string;
  traceId?: string;
  /**
   * Whether this utterance was cut off. The server treats an interrupted Turn as authorizing
   * nothing, and invalidates whatever confirmation was pending - a client can only ever use this to
   * make its own Turn less trusted.
   */
  interrupted?: boolean;
}

export interface SubmitTurnAcceptance {
  turnId: string;
  alreadyAccepted: boolean;
}

/**
 * Submitting a Turn either acknowledges accepted work still being processed, or - when this exact
 * native message was already submitted and answered - hands back that Turn's recorded terminal
 * Outcome directly, so a resubmission after a reconnect never has to poll for a result the
 * application already holds.
 */
export type SubmitTurnResult =
  | { kind: 'accepted'; acceptance: SubmitTurnAcceptance }
  | { kind: 'outcome'; outcome: TurnOutcomeView };

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

/** What the matches genuinely differ by, so an ambiguous answer can offer choices that change it. */
export interface StockNarrowingHints {
  units: string[];
  locations: string[];
  includesUnlocated: boolean;
}

export interface StockFindPayload {
  version: number;
  kind: 'stock_find';
  candidates: StockRowView[];
  hasMoreCandidates: boolean;
  narrowingHints: StockNarrowingHints;
}

/** One Stock Entry as it stands after a mutation. Quantities are exact decimal text, never numbers. */
export interface StockMutationEntryView {
  stockEntryId: string;
  name: string;
  unit: string;
  location: string | null;
  note: string | null;
  previousQuantity: string;
  quantity: string;
  created: boolean;
  /** True when a proposed Note was deliberately not applied because the Stock Entry already existed. */
  notePreserved: boolean;
}

export interface StockMutationPayload {
  version: number;
  kind: 'stock_mutation';
  operation: 'add' | 'remove' | 'set';
  entry: StockMutationEntryView;
}

/** One Stock Entry's before-and-after within a proposed or applied change. Quantities are exact decimal text, never numbers. */
export interface StockEntryStateView {
  stockEntryId: string | null;
  name: string;
  unit: string;
  location: string | null;
  note: string | null;
  previousQuantity: string;
  quantity: string;
  /** True when this Stock Entry's identity ends - merged away, or forgotten. */
  retired: boolean;
}

/** One change, exactly as proposed or exactly as applied. */
export interface StockChangeView {
  order: number;
  operation: 'add' | 'remove' | 'set' | 'move' | 'rename' | 'forget';
  effect:
    | 'created'
    | 'quantity_increased'
    | 'quantity_decreased'
    | 'quantity_set'
    | 'quantity_cleared'
    | 'placed'
    | 'split'
    | 'split_merged'
    | 'merged'
    | 'renamed'
    | 'rename_merged'
    | 'forgotten';
  source: StockEntryStateView;
  destination: StockEntryStateView | null;
  transferredQuantity: string;
  newName: string | null;
  /** The Stock Entry that still exists afterwards. */
  survivingStockEntryId: string | null;
  /** The Stock Entry whose identity this change ends, or null when it ends none. */
  retiredStockEntryId: string | null;
}

/**
 * An exact set of changes awaiting explicit confirmation. `token` is single-use and expires; it is
 * the only time the plaintext exists outside the server's own memory, and the server stores only its
 * hash.
 */
export interface StockProposalPayload {
  version: number;
  kind: 'stock_proposal';
  token: string;
  /** ISO-8601 round-trip instant, ten minutes after the proposal was made. */
  expiresAt: string;
  changes: StockChangeView[];
}

/** What one applied change set did. */
export interface StockChangesPayload {
  version: number;
  kind: 'stock_changes';
  changes: StockChangeView[];
}

export type TurnOutcomePayload =
  | StockListPayload
  | StockFindPayload
  | StockMutationPayload
  | StockProposalPayload
  | StockChangesPayload;

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
export async function submitTurn(request: SubmitTurnRequest, csrfToken: string): Promise<SubmitTurnResult> {
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

  if (response.status === 200) {
    return { kind: 'outcome', outcome: (await response.json()) as TurnOutcomeView };
  }

  return { kind: 'accepted', acceptance: (await response.json()) as SubmitTurnAcceptance };
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
