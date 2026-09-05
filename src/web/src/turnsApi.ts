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
 * An exact set of changes awaiting explicit confirmation.
 *
 * `token` is the single-use confirmation code. The server's proposal record keeps only its hash, but
 * this payload carries the plaintext, because the Participant has to quote it back and has to be able
 * to reconnect to the answer that gave it to them. It stops meaning anything once used or once the
 * proposal expires, and the server discards this payload at that same ten-minute mark - so treat it
 * as a short-lived secret: render it, do not log it, and do not persist it separately.
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

/** One active Unit: its stable identity, its canonical name, and its active aliases in order. */
export interface UnitView {
  id: string;
  name: string;
  aliases: string[];
}

/** One active Location. Flat and alias-free; unlocated stock is the absence of a reference and never appears here. */
export interface LocationView {
  id: string;
  name: string;
}

export interface UnitListPayload {
  version: number;
  kind: 'unit_list';
  units: UnitView[];
  nextCursor: string | null;
  hasMore: boolean;
}

export interface LocationListPayload {
  version: number;
  kind: 'location_list';
  locations: LocationView[];
  nextCursor: string | null;
  hasMore: boolean;
}

/** One Unit or Location administration change, exactly as proposed or exactly as applied. */
export interface ReferenceChangeView {
  order: number;
  operation:
    | 'create_unit'
    | 'rename_unit'
    | 'add_unit_alias'
    | 'remove_unit_alias'
    | 'retire_unit'
    | 'create_location'
    | 'rename_location'
    | 'retire_location';
  reference: 'unit' | 'location';
  /** The reference's stable identity. It never changes - not when renamed, and not when retired. */
  referenceId: string;
  name: string;
  newName: string | null;
  alias: string | null;
  aliases: string[];
}

/**
 * An exact set of reference changes awaiting explicit confirmation. `token` is the same short-lived
 * single-use confirmation code a stock proposal carries: render it, do not log it, and do not
 * persist it separately.
 */
export interface ReferenceProposalPayload {
  version: number;
  kind: 'reference_proposal';
  token: string;
  expiresAt: string;
  changes: ReferenceChangeView[];
}

/** What one applied administration change set did. */
export interface ReferenceChangesPayload {
  version: number;
  kind: 'reference_changes';
  changes: ReferenceChangeView[];
}

/**
 * The bounded, deterministic alternatives an unknown reference offers - active names sharing the
 * requested prefix, or else what this Inventory actually has. Never a nearest-match guess.
 */
export interface ReferenceSuggestionsPayload {
  version: number;
  kind: 'reference_suggestions';
  reference: 'unit' | 'location';
  suggestions: string[];
}

export type TurnOutcomePayload =
  | StockListPayload
  | StockFindPayload
  | StockMutationPayload
  | StockProposalPayload
  | StockChangesPayload
  | UnitListPayload
  | LocationListPayload
  | ReferenceProposalPayload
  | ReferenceChangesPayload
  | ReferenceSuggestionsPayload;

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

/**
 * Rebuilds the `TurnOutcomeView` shape the renderer already understands from the pieces the stream
 * delivers separately: the typed projection arrives as a data response part, and the terminal event
 * carries everything else. Keeping one shape is the point - the stream and the recovery endpoint
 * would otherwise need two renderers that could disagree about the same answer.
 */
export function composeOutcome(
  parts: { kind: 'text' | 'data'; text: string | null; payload: TurnOutcomePayload | null }[],
  terminal: {
    turnId: string;
    status: string;
    category: string;
    code: string;
    summary: string;
    deliveries: DeliveryView[];
  },
): TurnOutcomeView {
  return {
    turnId: terminal.turnId,
    status: terminal.status,
    category: terminal.category,
    code: terminal.code,
    summary: terminal.summary,
    payload: parts.find((part) => part.kind === 'data')?.payload ?? null,
    deliveries: terminal.deliveries,
  };
}
