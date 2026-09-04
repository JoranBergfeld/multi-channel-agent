/**
 * The typed client for the signed-in Initial Import workflow: whether it is offered here, what a
 * chosen CSV file would create, and the two decisions that end it.
 *
 * Every call names its own outcomes instead of collapsing to worked-or-failed, because "it failed"
 * is not something this workflow can act on. An expired proposal, a token that is no longer the
 * pending one, an Inventory that stopped being empty, and a server that is briefly unreachable each
 * ask for a different next step - and only some of them are safe to retry with the same preview.
 *
 * The payload shapes below are this client's assumption about the routes it calls, checked at
 * compile time against every use here and nowhere else: nothing validates a response body at
 * runtime, so a shape that drifts from the server's is a bug these types cannot catch.
 */

/** Whether Initial Import is available here, and when it is not, the one machine code saying why. */
export interface ImportEligibility {
  eligible: boolean;
  reason: string | null;
}

/** One Stock Entry the import would create, exactly as it will be created. */
export interface ImportPreviewRow {
  name: string;
  quantity: string;
  unitCanonicalName: string;
  locationName: string | null;
  note: string | null;
  /** The 1-based source lines that merged into this entry - the header is line 1. */
  sourceLineNumbers: number[];
}

export interface ImportPreview {
  /** The one-time plaintext token. It exists only here and in the confirmation that spends it. */
  token: string;
  proposalId: string;
  fileDigest: string;
  sourceRowCount: number;
  entries: ImportPreviewRow[];
  supersededPrevious: boolean;
  expiresAt: string;
}

/** One reported problem: its machine code, where it is, and any bounded suggestions. */
export interface ImportError {
  code: string;
  /** The 1-based source line, or 0 for a whole-file problem that belongs to no line. */
  lineNumber: number;
  /** The server's own zero-based column index, or null when the problem is about the whole record. */
  columnIndex: number | null;
  suggestions: string[];
}

export interface ImportErrorReport {
  errors: ImportError[];
  omittedErrorCount: number;
}

export interface ImportCompleted {
  proposalId: string;
  createdEntryCount: number;
  fileDigest: string;
}

/**
 * Every error the import contract can report, as the closed <c>ImportErrorCode</c> set the domain
 * defines. Naming them here is what makes the workflow's prose exhaustive at compile time: a code
 * listed without a sentence to render for it fails the build. An unrecognized code still renders -
 * see {@link isKnownImportErrorCode} - because the server, not this list, decides what it sends.
 */
export const IMPORT_ERROR_CODES = [
  'unknown_column',
  'duplicate_column',
  'wrong_column_count',
  'invalid_encoding',
  'unterminated_quote',
  'malformed_quote',
  'too_few_fields',
  'too_many_fields',
  'missing_name',
  'missing_quantity',
  'invalid_quantity',
  'quantity_overflow',
  'name_too_long',
  'note_too_long',
  'unit_too_long',
  'location_too_long',
  'unknown_unit',
  'unknown_location',
  'conflicting_notes',
  'file_too_large',
  'too_many_rows',
  'too_many_entries',
  'empty_file',
] as const;

export type ImportErrorCode = (typeof IMPORT_ERROR_CODES)[number];

export function isKnownImportErrorCode(code: string): code is ImportErrorCode {
  return (IMPORT_ERROR_CODES as readonly string[]).includes(code);
}

/**
 * Why a decision could not be applied. Both are final for the preview that met them - the proposal
 * is settled server-side either way - and they differ in what is true afterwards: an expired import
 * leaves an Inventory that is still empty, a changed state does not.
 */
export type ImportConflictCode = 'proposal_expired' | 'state_changed' | 'unknown';

/** Exactly one of these is present, so a caller cannot forget to handle a case. */
export type ImportValidation =
  | { kind: 'preview'; preview: ImportPreview }
  | { kind: 'errors'; report: ImportErrorReport }
  /** The upload itself could not be read as a CSV file part - no file, an empty one, or no token. */
  | { kind: 'unreadable-upload' }
  | { kind: 'not-empty' }
  | { kind: 'too-large' }
  | { kind: 'unavailable' };

/**
 * What a confirmation or a cancellation ran into. 'settled' means there is no pending import to act
 * on any more, so the preview showing it is stale; 'token-mismatch' deliberately leaves the proposal
 * pending, so the preview it belongs to is still worth keeping.
 */
type ImportDecisionRefusal =
  | { kind: 'settled' }
  | { kind: 'conflict'; code: ImportConflictCode }
  | { kind: 'token-mismatch' }
  | { kind: 'unavailable' };

export type ImportConfirmation = { kind: 'completed'; completed: ImportCompleted } | ImportDecisionRefusal;

export type ImportRejection = { kind: 'rejected' } | ImportDecisionRefusal;

const TOKEN_MISMATCH_CODE = 'proposal_token_mismatch';

const importUrl = (inventoryId: string) => `/api/inventories/${inventoryId}/import`;

/**
 * Reads whether Initial Import is offered for one Inventory.
 *
 * Only a 404 is an answer: an Inventory that does not exist, one this Participant may not edit, and
 * a session that has ended all report it with no body to read, deliberately indistinguishable, and
 * all mean the same thing here - not offered. Every other refusal says nothing about eligibility, so
 * it is raised rather than reported as one: a caller that read a 503 or an expired session as "not
 * offered" would discard a reviewed preview, and the one-time token that exists nowhere else, over a
 * server that was briefly unreachable. This is the same null-or-throw split every other client in
 * this directory uses; see `fetchStock` and `fetchUnits`.
 */
export async function fetchEligibility(inventoryId: string): Promise<ImportEligibility | null> {
  const response = await fetch(importUrl(inventoryId), { credentials: 'include' });

  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`Reading whether Initial Import is offered failed with status ${response.status}.`);
  }

  return (await response.json()) as ImportEligibility;
}

/**
 * Uploads one chosen file and answers with either the exact entries it would create - held as a
 * pending proposal for ten minutes - or every actionable problem it has. Nothing is created here.
 */
export async function validateImport(
  inventoryId: string,
  csrfToken: string,
  file: File,
): Promise<ImportValidation> {
  const body = new FormData();
  body.append('file', file, file.name);

  const response = await fetch(`${importUrl(inventoryId)}/validate`, {
    method: 'POST',
    credentials: 'include',
    headers: { 'X-CSRF-TOKEN': csrfToken },
    body,
  });

  if (response.ok) {
    return { kind: 'preview', preview: (await response.json()) as ImportPreview };
  }

  if (response.status === 413) {
    return { kind: 'too-large' };
  }

  if (response.status === 409) {
    return { kind: 'not-empty' };
  }

  if (response.status === 400) {
    const problem = await readProblem(response);

    // Two different 400s arrive here: the bounded error report, whose 'errors' is a list of coded
    // problems, and a validation problem about the upload itself, whose 'errors' is a map keyed by
    // part name. Only the first is a report about the file's contents, and only a list can be one.
    return Array.isArray(problem.errors)
      ? {
          kind: 'errors',
          report: {
            errors: problem.errors as ImportError[],
            omittedErrorCount: typeof problem.omittedErrorCount === 'number' ? problem.omittedErrorCount : 0,
          },
        }
      : { kind: 'unreadable-upload' };
  }

  return { kind: 'unavailable' };
}

/**
 * Spends the preview's one-time token to create exactly the previewed Stock Entries, atomically.
 *
 * The proposal id and the token are carried exactly as the preview issued them: the server matches
 * the token against a stored hash and the id against the one pending proposal, so a trimmed,
 * re-cased, or otherwise adjusted value is simply a different value. Confirming the same proposal
 * again re-reports what it did rather than importing twice, which is what makes retrying a request
 * that may or may not have reached the server safe.
 */
export async function confirmImport(
  inventoryId: string,
  csrfToken: string,
  proposalId: string,
  token: string,
): Promise<ImportConfirmation> {
  const response = await postDecision(`${importUrl(inventoryId)}/confirm`, csrfToken, proposalId, token);

  return response.ok
    ? { kind: 'completed', completed: (await response.json()) as ImportCompleted }
    : await refusalFor(response);
}

/**
 * Cancels the pending import, discarding its stored rows and its raw file. Nothing is created and
 * nothing existing is touched, so a cancellation that fails can always simply be repeated.
 */
export async function rejectImport(
  inventoryId: string,
  csrfToken: string,
  proposalId: string,
  token: string | null,
): Promise<ImportRejection> {
  const response = await postDecision(`${importUrl(inventoryId)}/reject`, csrfToken, proposalId, token);

  return response.ok ? { kind: 'rejected' } : await refusalFor(response);
}

function postDecision(
  url: string,
  csrfToken: string,
  proposalId: string,
  token: string | null,
): Promise<Response> {
  return fetch(url, {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
    body: JSON.stringify({ proposalId, token }),
  });
}

async function refusalFor(response: Response): Promise<ImportDecisionRefusal> {
  // An import that is not pending for this Participant and an Inventory they may not touch are one
  // answer on purpose, and it carries no body to read.
  if (response.status === 404) {
    return { kind: 'settled' };
  }

  if (response.status === 409 || response.status === 400) {
    const code = readCode(await readProblem(response));

    if (response.status === 409) {
      return { kind: 'conflict', code: toConflictCode(code) };
    }

    // A mismatched token leaves the proposal pending by design, so reviewed work survives a typo or
    // a superseding upload. Any other 400 - a rejected CSRF token, an unreadable body - says nothing
    // about the proposal, so it is reported as what it is rather than blamed on the token.
    return code === TOKEN_MISMATCH_CODE ? { kind: 'token-mismatch' } : { kind: 'unavailable' };
  }

  return { kind: 'unavailable' };
}

function toConflictCode(code: string | null): ImportConflictCode {
  return code === 'proposal_expired' || code === 'state_changed' ? code : 'unknown';
}

/**
 * Reads a refusal's problem document, tolerating one that has none. A refusal is allowed to answer
 * with an empty body, and an intermediary is allowed to answer with HTML; neither says anything the
 * status code has not already said, and neither may become an exception a caller did not ask for.
 */
async function readProblem(response: Response): Promise<Record<string, unknown>> {
  try {
    const body: unknown = await response.json();

    return typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

function readCode(problem: Record<string, unknown>): string | null {
  return typeof problem.code === 'string' ? problem.code : null;
}
