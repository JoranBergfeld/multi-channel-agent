import { useCallback, useEffect, useState } from 'react';
import {
  confirmImport,
  fetchEligibility,
  isKnownImportErrorCode,
  rejectImport,
  validateImport,
  type ImportConflictCode,
  type ImportError,
  type ImportErrorCode,
  type ImportErrorReport,
  type ImportPreview,
  type ImportValidation,
} from './importApi';

interface InitialImportProps {
  inventoryId: string;
  csrfToken: string;
  /** Bumped by the parent whenever Stock may have changed, to re-read whether importing is still offered. */
  refetchToken: number;
  /**
   * Called once, on each path where the server has just said Stock here is not what this workspace is
   * showing, so it refetches: an import that completed, an Inventory the server says is no longer
   * empty - when validating or when confirming meets `state_changed` - and either decision answered
   * with a settled proposal, because neither 404 can rule out an import confirmed somewhere else.
   *
   * Deliberately not called where nothing the server said implies Stock moved: an ordinary
   * cancellation, a token that did not match, an expired proposal, a file that was refused, or a
   * request that failed. Nothing in the eligibility effect calls this either, so the refetch it asks
   * for - which reaches this component back as a bumped `refetchToken` - cannot ask for another.
   */
  onStockMayHaveChanged: () => void;
}

/** What one in-flight request is doing, so a button can say so and no second one can start. */
type ImportAction = 'validating' | 'confirming' | 'cancelling';

/**
 * The one sentence on screen, and where it came from, because the two are not superseded alike. A
 * background eligibility read's failure describes that read, so the next read that answers has
 * replaced it, and a decision's result has not. A decision's result describes Stock that was or was
 * not created, and has to outlive both the eligibility re-read that same decision deliberately asks
 * for and a later read that could not be made at all.
 */
type ImportAlert = { source: 'background' | 'action'; message: string };

const actionAlert = (message: string): ImportAlert => ({ source: 'action', message });

const backgroundAlert = (message: string): ImportAlert => ({ source: 'background', message });

const HEADING_ID = 'initialImportHeading';
const FILE_INPUT_ID = 'initialImportFile';

/** The five columns, in order, so an error naming a column index can name the column a person sees. */
const COLUMNS = ['Name', 'Quantity', 'Unit', 'Location', 'Note'];

/**
 * One readable sentence per machine code. The server sends codes and never prose, so this is the one
 * place an import failure is worded - and being keyed by the closed set of codes, a code the client
 * knows about without a sentence to render for it cannot compile.
 */
const ERROR_MESSAGES: Record<ImportErrorCode, string> = {
  unknown_column: 'That column is not one of the five this import accepts.',
  duplicate_column: 'That column appears more than once.',
  wrong_column_count: 'The file must have exactly five columns: Name, Quantity, Unit, Location, Note.',
  invalid_encoding: 'The file is not valid UTF-8 text.',
  unterminated_quote: 'A quoted value is never closed.',
  malformed_quote: 'A quoted value is followed by unexpected text.',
  too_few_fields: 'This line has fewer than five values.',
  too_many_fields: 'This line has more than five values.',
  missing_name: 'Name is required.',
  missing_quantity: 'Quantity is required.',
  invalid_quantity: 'Quantity must be a plain non-negative number, for example 10 or 2.5.',
  quantity_overflow: 'The quantities on these equivalent lines add up to more than can be stored.',
  name_too_long: 'That name is too long.',
  note_too_long: 'That Note is too long.',
  unit_too_long: 'That Unit name is too long.',
  location_too_long: 'That Location name is too long.',
  unknown_unit: 'No active Unit here answers to that name. Create it first; an import never creates one.',
  unknown_location: 'No active Location here carries that name. Create it first; an import never creates one.',
  conflicting_notes: 'Equivalent lines carry different Notes, so they cannot be merged into one Stock Entry.',
  file_too_large: 'That file is larger than 2 MiB.',
  too_many_rows: 'That file has more than 5,000 rows.',
  too_many_entries: 'That file would create more than 5,000 Stock Entries.',
  empty_file: 'That file has no rows to import.',
};

/** Why a decision could not be applied, in the same one-sentence-per-code shape. */
const CONFLICT_MESSAGES: Record<ImportConflictCode, string> = {
  proposal_expired:
    'That preview expired before it was confirmed, so nothing was created. Choose the file again to preview it afresh.',
  state_changed:
    'This Inventory stopped being empty while that preview was open, so nothing was created. ' +
    'Initial Import is offered only while an Inventory has no Stock Entries.',
  unknown: 'That import can no longer be applied, and nothing was created.',
};

/**
 * What a 404 to either decision means, and all it means. The server settles a proposal and stops
 * knowing it was ever pending, so "no pending import for you here" covers one cancelled in another
 * tab, one that expired, one a newer upload superseded - and one that was confirmed elsewhere,
 * whether this very proposal or the newer one that replaced it. Neither decision's reply tells those
 * apart, so this sentence names them and claims none.
 */
const SETTLED_MESSAGE =
  'That import is no longer pending - it may have been cancelled, it may have expired, or an import may ' +
  'already have been confirmed here. The Inventory view has been refreshed to show what is here now.';

/**
 * The signed-in Initial Import workflow: offered only while this Inventory has no Stock Entries, it
 * validates a chosen CSV file, shows either every actionable problem or the exact normalized Stock
 * Entries it would create, and creates them only on an explicit confirmation.
 *
 * Nothing here is the authorization boundary or the empty-Inventory rule - both are re-decided by
 * the server on every call, including inside the transaction that creates the entries. What this
 * component owns is that a Participant is never shown a preview they cannot act on, never left with
 * a control that stays disabled because a request failed, and never told an import happened, or did
 * not, without the server having said so.
 */
function InitialImport({ inventoryId, csrfToken, refetchToken, onStockMayHaveChanged }: InitialImportProps) {
  const [eligible, setEligible] = useState<boolean | null>(null);
  const [validation, setValidation] = useState<ImportValidation | null>(null);
  const [busy, setBusy] = useState<ImportAction | null>(null);
  const [completedEntryCount, setCompletedEntryCount] = useState<number | null>(null);
  const [alert, setAlert] = useState<ImportAlert | null>(null);

  /**
   * Whether the server offers this workflow here, as a plain answer. A null eligibility is the one
   * authoritative refusal - a 404, which an Inventory that does not exist and one this Participant
   * may not edit share - so collapsing it to false is naming what it means. Every other refusal,
   * including an ended session's 401, is raised by the client instead of answered, so no caller of
   * this can read a transient failure as a decision.
   */
  const readEligibility = useCallback(
    async () => (await fetchEligibility(inventoryId))?.eligible ?? false,
    [inventoryId],
  );

  const applyEligibility = useCallback((available: boolean) => {
    setEligible(available);

    if (!available) {
      // A preview the server will no longer accept can never be confirmed, so it stops being offered
      // the moment the server says so - whether this Inventory stopped being empty or stopped being
      // this Participant's to change. Only an answer reaches here: a failed read raises instead, and
      // is caught below with the preview, its token, and the current offer left exactly as they are.
      // Anything else being shown is a report about a file rather than an offer, and stays readable.
      setValidation((current) => (current?.kind === 'preview' ? null : current));
    }
  }, []);

  /** Asks the server what is true now. Used after anything that may have ended this workflow. */
  const refreshEligibility = useCallback(async () => {
    try {
      applyEligibility(await readEligibility());
    } catch {
      // Whatever prompted this re-check has already said what failed, and a stale eligibility is
      // safe here: every route re-decides it anyway, and this one is only about what to offer.
    }
  }, [applyEligibility, readEligibility]);

  useEffect(() => {
    let ignored = false;

    // oxlint(react/set-state-in-effect) only recognizes an inline async IIFE's await boundary, so the
    // work stays inline here; see StockWorkspace.tsx for the same pattern.
    void (async () => {
      try {
        const available = await readEligibility();

        // An answer that arrives after this effect has been replaced describes an Inventory, or a
        // moment, that nothing on screen is about any more.
        if (!ignored) {
          applyEligibility(available);

          // A read that answered is what the last failed read could not give, so its message has
          // stopped describing anything and goes. Only a background read's own failure is cleared
          // here, and deliberately not in applyEligibility: confirming and cancelling re-read
          // eligibility on purpose, and clearing more than this would erase the answer they just
          // gave - including the one that says the workspace was refetched.
          setAlert((current) => (current?.source === 'background' ? null : current));
        }
      } catch (failure) {
        // A re-check that could not be made is not an answer, so nothing on screen moves: the offer,
        // any reviewed preview, and the one-time token that only exists in it all stay exactly as
        // they were, and the next bump of refetchToken asks again.
        if (!ignored) {
          // A decision's result outranks this. A background read that failed knows nothing about
          // what a confirmation or a cancellation was just told, so overwriting that answer with
          // this one would replace the only report of what happened to this Participant's import
          // with a sentence about a read nobody asked for. An earlier background failure it may
          // replace: both describe the same re-check, and this is the newer one.
          setAlert((current) =>
            current?.source === 'action'
              ? current
              : backgroundAlert(
                  `Checking whether Initial Import is available failed: ${describeFailure(failure)} ` +
                    'Any preview already on screen is untouched.',
                ),
          );
        }
      }
    })();

    return () => {
      ignored = true;
    };
    // refetchToken deliberately participates in this effect's dependency list purely to trigger the
    // re-read when it changes - its value itself is never read.
  }, [applyEligibility, readEligibility, refetchToken]);

  /**
   * What both decisions do with a 404, because it means the same thing to each: the proposal this
   * page is holding a preview of is settled, and this reply will not say by what. So the preview and
   * its spent one-time token go, the offer is re-read from the server rather than inferred, and the
   * workspace is told Stock may have moved - because an import confirmed in another tab, of this
   * proposal or of the one that superseded it, is one of the things this answer covers.
   */
  async function reportSettledProposal() {
    setValidation(null);
    setAlert(actionAlert(SETTLED_MESSAGE));
    await refreshEligibility();
    onStockMayHaveChanged();
  }

  /**
   * What both decisions do with a 409: the proposal is settled server-side either way, so its
   * preview can never be confirmed. Only one of the codes says anything about Stock - a changed
   * state is the server reporting this Inventory stopped being empty, which is Stock this workspace
   * was not showing - and an expired proposal created nothing, so it asks for no refetch.
   */
  async function reportConflict(code: ImportConflictCode) {
    setValidation(null);
    setAlert(actionAlert(CONFLICT_MESSAGES[code]));
    await refreshEligibility();

    if (code === 'state_changed') {
      onStockMayHaveChanged();
    }
  }

  async function handleFile(event: React.ChangeEvent<HTMLInputElement>) {
    const input = event.target;
    const file = input.files?.[0];

    // Clearing the control's own value lets the same file be chosen again after it is fixed on disk,
    // and means the control never claims a file that is no longer what is being previewed.
    input.value = '';

    if (!file) {
      return;
    }

    setBusy('validating');
    setAlert(null);

    // Choosing a file starts over. Whatever the previous answer was, this upload supersedes the
    // proposal behind it, so it must not be left on screen next to a newer one.
    setValidation(null);
    setCompletedEntryCount(null);

    try {
      const result = await validateImport(inventoryId, csrfToken, file);
      setValidation(result);

      if (result.kind === 'not-empty') {
        // Validating creates nothing, but this refusal is the server saying this Inventory already
        // holds Stock - and this file control is offered only while the last eligibility read said
        // it had none, so the workspace beside it may still be showing that empty Inventory.
        await refreshEligibility();
        onStockMayHaveChanged();
      }
    } catch (failure) {
      setAlert(
        actionAlert(
          `Reading that file failed: ${describeFailure(failure)} Nothing was created - choose the file again.`,
        ),
      );
    } finally {
      setBusy(null);
    }
  }

  async function handleConfirm(preview: ImportPreview) {
    setBusy('confirming');
    setAlert(null);

    try {
      const result = await confirmImport(inventoryId, csrfToken, preview.proposalId, preview.token);

      switch (result.kind) {
        case 'completed':
          setValidation(null);
          setCompletedEntryCount(result.completed.createdEntryCount);

          // This Inventory holds Stock now, so the workflow is over here. Saying so immediately keeps
          // the file control from being offered for the moment it takes the parent's refetch - which
          // onStockMayHaveChanged triggers, and which re-reads this from the server - to agree.
          setEligible(false);
          onStockMayHaveChanged();
          return;

        case 'conflict':
          await reportConflict(result.code);
          return;

        case 'settled':
          // Confirming replays an import that already ran rather than refusing it - the server
          // answers from its ledger before it looks for a pending proposal - but that ledger is
          // keyed to *this* proposal, so this 404 says only that this proposal never ran. A newer
          // upload that superseded it and was confirmed in another tab leaves exactly this answer
          // behind, with the Stock it created already here, so this cannot claim nothing exists.
          await reportSettledProposal();
          return;

        case 'token-mismatch':
          // The proposal is deliberately left pending, so the reviewed preview is still worth having.
          setAlert(
            actionAlert(
              'That confirmation was refused because it did not match the pending import. ' +
                'Choose the file again to preview it afresh.',
            ),
          );
          return;

        case 'unavailable':
          // Confirming the same proposal again re-reports what it did rather than importing twice, so
          // retrying is safe even if this request did reach the server.
          setAlert(
            actionAlert('Confirming failed. Nothing has been created unless a retry says so - try confirming again.'),
          );
          return;

        default:
          return assertNever(result);
      }
    } catch (failure) {
      setAlert(
        actionAlert(`Confirming failed: ${describeFailure(failure)} Try confirming again - it can never import twice.`),
      );
    } finally {
      setBusy(null);
    }
  }

  async function handleCancel(preview: ImportPreview) {
    setBusy('cancelling');
    setAlert(null);

    try {
      const result = await rejectImport(inventoryId, csrfToken, preview.proposalId, preview.token);

      switch (result.kind) {
        case 'rejected':
          // Only now: until the server has settled the proposal and discarded its file, this preview
          // is still the pending import, and hiding it would be claiming otherwise.
          setValidation(null);
          return;

        case 'settled':
          // Cancelling, unlike confirming, does not replay anything: the server looks for a pending
          // proposal and answers 404 when there is none. Neither reply distinguishes a cancellation
          // in another tab, an expiry, a superseding upload, or an import that was confirmed
          // elsewhere - so both are answered the same way, and neither invents which it was.
          await reportSettledProposal();
          return;

        case 'conflict':
          await reportConflict(result.code);
          return;

        case 'token-mismatch':
          setAlert(
            actionAlert(
              'That cancellation was refused because it did not match the pending import. ' +
                'Nothing was created - choose the file again to preview it afresh.',
            ),
          );
          return;

        case 'unavailable':
          // What the proposal is now is exactly what this refusal does not say, and a cancellation
          // that did settle before the answer was lost simply answers 'settled' when it is repeated.
          setAlert(
            actionAlert(
              'Cancelling failed, so this import may still be pending. Cancelling never creates anything - ' +
                'try cancelling again.',
            ),
          );
          return;

        default:
          return assertNever(result);
      }
    } catch (failure) {
      setAlert(
        actionAlert(`Cancelling failed: ${describeFailure(failure)} Nothing was created - try cancelling again.`),
      );
    } finally {
      setBusy(null);
    }
  }

  // Before the first eligibility answer there is nothing to offer and nothing to report, and this
  // workflow is not what a Participant came to the page for.
  if (eligible === null && alert === null && completedEntryCount === null) {
    return null;
  }

  return (
    <section aria-labelledby={HEADING_ID}>
      <h2 id={HEADING_ID}>Initial Import</h2>

      {alert !== null && <p role="alert">{alert.message}</p>}

      {completedEntryCount !== null && (
        <p role="status">Imported {entryCountLabel(completedEntryCount)}, exactly as previewed.</p>
      )}

      {eligible === false && completedEntryCount === null && (
        <p>
          Initial Import is offered only while an Inventory has no Stock Entries and you may change it. Add Stock
          through the conversation instead.
        </p>
      )}

      {/*
        An import that has already run here is final for this workspace: a file is offered only while
        the server says this Inventory is empty and nothing has been imported into it from this page.
        The second half is what an eligibility read still in flight when the import completed cannot
        undo, so a completed import can never be followed by an offer to import again.
      */}
      {eligible === true && completedEntryCount === null && (
        <>
          <p>
            Choose a UTF-8 CSV file with exactly the columns {COLUMNS.join(', ')}, in that order. A blank Unit
            means <code>each</code>, a blank Location means unlocated, and a blank Note means no Note. Up to 2 MiB,
            5,000 rows, and 5,000 Stock Entries. Every Unit and Location it names must already exist here.
          </p>
          <label htmlFor={FILE_INPUT_ID}>CSV file to preview</label>
          <input
            id={FILE_INPUT_ID}
            type="file"
            accept=".csv,text/csv"
            onChange={(event) => void handleFile(event)}
            disabled={busy !== null}
            aria-busy={busy === 'validating'}
          />
          {busy === 'validating' && <p role="status">Checking that file…</p>}
        </>
      )}

      {validation !== null && (
        <ValidationOutcome
          validation={validation}
          busy={busy}
          onConfirm={(preview) => void handleConfirm(preview)}
          onCancel={(preview) => void handleCancel(preview)}
        />
      )}
    </section>
  );
}

interface OutcomeProps {
  validation: ImportValidation;
  busy: ImportAction | null;
  onConfirm: (preview: ImportPreview) => void;
  onCancel: (preview: ImportPreview) => void;
}

/** One answer per validation outcome. A new outcome without an answer here cannot compile. */
function ValidationOutcome({ validation, busy, onConfirm, onCancel }: OutcomeProps) {
  switch (validation.kind) {
    case 'preview':
      return (
        <PreviewOutcome preview={validation.preview} busy={busy} onConfirm={onConfirm} onCancel={onCancel} />
      );

    case 'errors':
      return <ErrorOutcome report={validation.report} />;

    case 'not-empty':
      return <p role="alert">This Inventory already holds Stock, so there is nothing initial to import.</p>;

    case 'too-large':
      return <p role="alert">That file is larger than 2 MiB, so it was not read at all.</p>;

    case 'unreadable-upload':
      return (
        <p role="alert">
          That upload could not be read. Choose a single, non-empty CSV file and try again.
        </p>
      );

    case 'unavailable':
      return <p role="alert">Initial Import is not available right now. Try again in a moment.</p>;

    default:
      return assertNever(validation);
  }
}

/** The exact Stock Entries the import would create, and the only two ways to leave this screen. */
function PreviewOutcome({
  preview,
  busy,
  onConfirm,
  onCancel,
}: {
  preview: ImportPreview;
  busy: ImportAction | null;
  onConfirm: (preview: ImportPreview) => void;
  onCancel: (preview: ImportPreview) => void;
}) {
  const entryCount = preview.entries.length;

  return (
    <>
      <h3>
        {entryCountLabel(entryCount)} from {preview.sourceRowCount}{' '}
        {preview.sourceRowCount === 1 ? 'row' : 'rows'}
      </h3>
      <p>
        This is exactly what would be created, with equivalent rows already merged. Nothing exists yet, and nothing
        will until you confirm.
      </p>

      {preview.supersededPrevious && (
        <p>This preview replaced your previous pending Initial Import here. Only this one can still be confirmed.</p>
      )}

      <ExpiryNotice expiresAt={preview.expiresAt} />

      <table>
        <caption>The exact Stock Entries this import would create</caption>
        <thead>
          <tr>
            <th scope="col">Name</th>
            <th scope="col">Quantity</th>
            <th scope="col">Unit</th>
            <th scope="col">Location</th>
            <th scope="col">Note</th>
            <th scope="col">From lines</th>
          </tr>
        </thead>
        <tbody>
          {preview.entries.map((entry, index) => (
            <tr key={`${index}-${entry.name}-${entry.unitCanonicalName}-${entry.locationName ?? ''}`}>
              <td>{entry.name}</td>
              <td>{entry.quantity}</td>
              <td>{entry.unitCanonicalName}</td>
              <td>{entry.locationName ?? 'Unlocated'}</td>
              <td>{entry.note ?? '—'}</td>
              <td>{entry.sourceLineNumbers.join(', ')}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <button
        type="button"
        onClick={() => onConfirm(preview)}
        disabled={busy !== null}
        aria-busy={busy === 'confirming'}
      >
        {busy === 'confirming' ? 'Importing…' : `Import ${entryCountLabel(entryCount)}`}
      </button>
      <button
        type="button"
        onClick={() => onCancel(preview)}
        disabled={busy !== null}
        aria-busy={busy === 'cancelling'}
      >
        {busy === 'cancelling' ? 'Cancelling…' : 'Cancel this import'}
      </button>
    </>
  );
}

/** Every actionable problem at once, plus the exact number the bounded report left out. */
function ErrorOutcome({ report }: { report: ImportErrorReport }) {
  return (
    <>
      <h3>That file was not imported</h3>

      {report.errors.length === 0 ? (
        <p role="alert">That file could not be imported, and no report of why arrived. Nothing was created.</p>
      ) : (
        <>
          <p role="alert">Fix every problem below, then choose the file again. Nothing was created.</p>
          <table>
            <caption>Everything wrong with that file</caption>
            <thead>
              <tr>
                <th scope="col">Line</th>
                <th scope="col">Column</th>
                <th scope="col">Problem</th>
              </tr>
            </thead>
            <tbody>
              {report.errors.map((error, index) => (
                <tr key={`${index}-${error.lineNumber}-${error.code}`}>
                  <td>{error.lineNumber > 0 ? error.lineNumber : '—'}</td>
                  <td>{columnLabel(error.columnIndex)}</td>
                  <td>{describeError(error)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}

      {report.omittedErrorCount > 0 && (
        <p>
          And {report.omittedErrorCount} more{' '}
          {report.omittedErrorCount === 1 ? 'problem' : 'problems'} this report left out.
        </p>
      )}
    </>
  );
}

/** When the preview stops being confirmable, so a long review is not spent on a dead proposal. */
function ExpiryNotice({ expiresAt }: { expiresAt: string }) {
  const expiry = new Date(expiresAt);

  if (Number.isNaN(expiry.getTime())) {
    return null;
  }

  return (
    <p>
      Confirm by <time dateTime={expiresAt}>{expiry.toLocaleTimeString()}</time>. After that this preview expires,
      the file is discarded, and nothing is created.
    </p>
  );
}

function describeError(error: ImportError): string {
  const message = isKnownImportErrorCode(error.code)
    ? ERROR_MESSAGES[error.code]
    : `That line was refused (${error.code}).`;

  return error.suggestions.length > 0 ? `${message} Did you mean: ${error.suggestions.join(', ')}?` : message;
}

/**
 * Names the column an error is about. The index is the server's own zero-based column position, so
 * one outside the five columns is reported as the number it is rather than rendered as nothing.
 */
function columnLabel(columnIndex: number | null): string {
  if (columnIndex === null || columnIndex < 0) {
    return '—';
  }

  return columnIndex < COLUMNS.length ? COLUMNS[columnIndex] : `Column ${columnIndex + 1}`;
}

function entryCountLabel(count: number): string {
  return `${count} Stock ${count === 1 ? 'Entry' : 'Entries'}`;
}

function describeFailure(failure: unknown): string {
  const message = failure instanceof Error ? failure.message : String(failure);

  return message.endsWith('.') ? message : `${message}.`;
}

/** Fails to compile the day an outcome is added without an answer for it above. */
function assertNever(value: never): never {
  throw new Error(`Unhandled Initial Import outcome: ${JSON.stringify(value)}.`);
}

export default InitialImport;
