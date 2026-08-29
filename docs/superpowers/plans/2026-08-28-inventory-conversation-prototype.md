# Inventory Conversation Prototype Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a single-file interactive prototype that makes the approved text-and-voice Inventory conversation policy tangible through guided walkthroughs and free play.

**Architecture:** Create one self-contained HTML file with inline CSS and JavaScript. Keep the domain behavior in a pure `ConversationPrototype` module whose reducer accepts state and actions without touching the DOM; a thin page shell renders state, dispatches actions, and manages walkthrough tabs.

**Tech Stack:** HTML5, CSS, browser-native JavaScript; no framework, package manager, persistence, network service, or automated test runner.

---

## File Structure

- Create `prototypes/inventory-conversation-prototype.html`: the entire shareable prototype, containing:
  - restrained page styles;
  - the pure conversation reducer and scenario fixtures;
  - guided walkthrough definitions;
  - rendering and event-binding code.
- Modify `docs/superpowers/specs/2026-08-28-inventory-conversation-prototype-design.md`: replace the future-tense capture statement after the HTML artifact exists.
- Modify GitHub issue **Prototype the inventory mutation conversation in text and voice**: link the branch and commit after the artifact is captured; this tracker update is not a repository file.

The prototype remains one file because direct browser opening and easy sharing are explicit requirements. The reducer stays isolated inside that file so its behavior can be evaluated independently from the page shell.

### Task 1: Create the Page Shell and Seed Inventory

**Files:**
- Create: `prototypes/inventory-conversation-prototype.html`

- [ ] **Step 1: Create the semantic page shell**

Add the document structure below. The page must state the question visibly and provide dedicated regions for the scenario tabs, transcript, conversation state, Inventory state, transition explanation, walkthrough controls, and free-play controls.

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Inventory Conversation Prototype</title>
    <style>
      /* Task 4 replaces this marker with the complete restrained styling. */
    </style>
  </head>
  <body>
    <main class="page">
      <header class="intro">
        <p class="eyebrow">Throwaway logic prototype</p>
        <h1>Inventory conversations in text and voice</h1>
        <p>
          This prototype tests whether one balanced mutation policy can stay
          predictable across text and voice while adapting confirmations,
          clarification, and result length to each channel.
        </p>
      </header>

      <nav id="scenario-tabs" class="scenario-tabs" aria-label="Guided walkthroughs"></nav>

      <section class="workspace">
        <article class="conversation-panel">
          <div class="panel-heading">
            <div>
              <p class="eyebrow">Conversation</p>
              <h2 id="scenario-title"></h2>
            </div>
            <div class="channel-switch" aria-label="Channel">
              <button type="button" data-channel="text">Text</button>
              <button type="button" data-channel="voice">Voice</button>
            </div>
          </div>
          <p id="scenario-description" class="muted"></p>
          <ol id="transcript" class="transcript" aria-live="polite"></ol>
          <div id="walkthrough-controls" class="controls"></div>
        </article>

        <aside class="state-column">
          <section class="state-panel">
            <p class="eyebrow">Conversation state</p>
            <dl id="conversation-state"></dl>
          </section>
          <section class="state-panel">
            <p class="eyebrow">Inventory state</p>
            <div id="inventory-state"></div>
          </section>
          <section class="state-panel transition-panel">
            <p class="eyebrow">What changed</p>
            <p id="transition-explanation"></p>
          </section>
        </aside>
      </section>

      <section class="free-play">
        <div>
          <p class="eyebrow">Free play</p>
          <h2>Push the awkward transitions</h2>
          <p class="muted">
            These controls dispatch through the same reducer as the guided walkthroughs.
          </p>
        </div>
        <div id="free-play-controls" class="free-play-grid"></div>
      </section>
    </main>

    <script>
      /* Tasks 1-3 add the pure module, walkthroughs, and page shell here. */
    </script>
  </body>
</html>
```

- [ ] **Step 2: Define the seed Stock Entries**

At the start of the `<script>` block, add an immutable fixture factory. Use stable identifiers because clarification and mutations must target Stock Entries independently from their display names.

```js
const createSeedInventory = () => ({
  entries: [
    { id: "gloves", name: "Nitrile gloves", quantity: 6, unit: "boxes", location: "Garage", note: "" },
    { id: "aa-batteries", name: "AA batteries", quantity: 24, unit: "each", location: "Garage", note: "" },
    { id: "aaa-batteries", name: "AAA batteries", quantity: 18, unit: "each", location: "Utility room", note: "" },
    { id: "white-paint", name: "White wall paint", quantity: 2, unit: "cans", location: "Garage", note: "" },
    { id: "blue-paint", name: "Blue wall paint", quantity: 1, unit: "can", location: "Garage", note: "" },
    { id: "primer", name: "Primer", quantity: 3, unit: "cans", location: "Garage", note: "For wall paint" },
    { id: "screws", name: "Wood screws", quantity: 48, unit: "each", location: "Garage", note: "40 mm" },
    { id: "filters", name: "Air filters", quantity: 4, unit: "boxes", location: "Garage", note: "" },
    { id: "tape", name: "Masking tape", quantity: 8, unit: "rolls", location: "Garage", note: "" },
    { id: "sandpaper", name: "Sandpaper", quantity: 12, unit: "sheets", location: "Garage", note: "120 grit" },
    { id: "oil", name: "Machine oil", quantity: 2, unit: "bottles", location: "Garage", note: "" },
    { id: "brushes", name: "Paint brushes", quantity: 7, unit: "each", location: "Garage", note: "" },
    { id: "rags", name: "Cleaning rags", quantity: 15, unit: "each", location: "Garage", note: "" },
    { id: "anchors", name: "Wall anchors", quantity: 30, unit: "each", location: "Garage", note: "" },
    { id: "sealant", name: "Silicone sealant", quantity: 5, unit: "tubes", location: "Garage", note: "" },
    { id: "extension-cords", name: "Extension cords", quantity: 3, unit: "each", location: "Garage", note: "" },
  ],
});
```

- [ ] **Step 3: Open the initial shell**

Run:

```bash
xdg-open prototypes/inventory-conversation-prototype.html
```

Expected: a browser opens the document. Unstyled headings and empty regions are acceptable at this step; there must be no browser syntax error.

- [ ] **Step 4: Commit the shell and fixtures**

```bash
git add prototypes/inventory-conversation-prototype.html
git commit -m "prototype: add inventory conversation shell" \
  -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 2: Implement the Pure Conversation Reducer

**Files:**
- Modify: `prototypes/inventory-conversation-prototype.html`

- [ ] **Step 1: Add state factories and immutable update helpers**

Insert these definitions after `createSeedInventory`. They establish the exact property names used by every later task.

```js
const ConversationStatus = Object.freeze({
  IDLE: "idle",
  UNDERSTANDING: "understanding",
  CLARIFYING: "clarifying",
  CONFIRMING: "confirming",
  APPLIED: "applied",
  FAILED: "failed",
});

const createInitialState = (channel = "text") => ({
  inventory: createSeedInventory(),
  channel,
  status: ConversationStatus.IDLE,
  pendingIntent: null,
  candidates: [],
  proposedMutations: [],
  uncertainFields: [],
  transcript: [],
  lastResponse: "Choose a walkthrough or a free-play action.",
  transition: "The prototype is ready.",
});

const updateEntry = (inventory, entryId, update) => ({
  ...inventory,
  entries: inventory.entries.map((entry) =>
    entry.id === entryId ? { ...entry, ...update(entry) } : entry
  ),
});

const appendTurn = (state, speaker, text) => ({
  ...state,
  transcript: [...state.transcript, { speaker, text }],
});

const withAgentResponse = (state, text, transition) => ({
  ...appendTurn(state, "Agent", text),
  lastResponse: text,
  transition,
});

const formatQuantity = (quantity, unit) => `${quantity} ${unit}`;
```

- [ ] **Step 2: Add channel-adapted response functions**

Keep presentation outside the reducer branches so policy and wording remain distinguishable.

```js
const Responses = {
  added(channel, entry, amount, resultingQuantity) {
    if (channel === "voice") {
      return `Added ${formatQuantity(amount, entry.unit)} in the ${entry.location.toLowerCase()}. You now have ${resultingQuantity}.`;
    }
    return `Added ${formatQuantity(amount, entry.unit)} of ${entry.name} in the ${entry.location}. You now have ${formatQuantity(resultingQuantity, entry.unit)}.`;
  },

  ambiguousBatteries(channel) {
    if (channel === "voice") {
      return "AA batteries in the garage, or AAA batteries in the utility room?";
    }
    return "Which batteries: 1. AA batteries — 24 each, Garage; or 2. AAA batteries — 18 each, Utility room?";
  },

  removed(channel, entry, amount, resultingQuantity) {
    if (channel === "voice") {
      return `Removed ${amount}. ${resultingQuantity} ${entry.name} remain in the ${entry.location.toLowerCase()}.`;
    }
    return `Removed ${formatQuantity(amount, entry.unit)} of ${entry.name} from the ${entry.location}. ${formatQuantity(resultingQuantity, entry.unit)} remain.`;
  },

  bulkPreview(channel, mutations) {
    if (channel === "voice") {
      const names = mutations.map((mutation) => mutation.name.toLowerCase());
      return `That affects ${mutations.length} entries: ${names.slice(0, -1).join(", ")}, and ${names.at(-1)}. All would become zero. Should I apply that?`;
    }
    const details = mutations
      .map((mutation) => `${mutation.name}: ${mutation.before} ${mutation.unit} → 0`)
      .join("; ");
    return `This will set ${mutations.length} Stock Entries to zero: ${details}. Apply these changes?`;
  },

  bulkApplied(channel, count) {
    return channel === "voice"
      ? `Done. All ${count} entries are now zero.`
      : `Applied the bulk Set. ${count} Stock Entries are now zero.`;
  },

  garageList(channel, entries) {
    if (channel === "voice") {
      const largest = [...entries]
        .sort((left, right) => right.quantity - left.quantity)
        .slice(0, 3)
        .map((entry) => `${entry.quantity} ${entry.name.toLowerCase()}`);
      return `You have ${entries.length} on-hand entries in the garage. The largest quantities are ${largest.join(", ")}. Want the full list, or should I narrow it?`;
    }
    return entries
      .map((entry) => `${entry.name}: ${formatQuantity(entry.quantity, entry.unit)}`)
      .join(" · ");
  },

  uncertainQuantity() {
    return "Was that fifteen boxes or fifty boxes?";
  },

  insufficient(entry, requested) {
    return `I couldn't remove ${requested}. Only ${formatQuantity(entry.quantity, entry.unit)} of ${entry.name} are on hand.`;
  },
};
```

- [ ] **Step 3: Add the reducer action contract**

Define actions as plain objects with these shapes:

```js
// { type: "SET_CHANNEL", channel: "text" | "voice" }
// { type: "START_ADD" }
// { type: "START_AMBIGUOUS_REMOVE" }
// { type: "CHOOSE_MATCH", entryId: string }
// { type: "START_BULK_SET" }
// { type: "CONFIRM_MUTATION" }
// { type: "CANCEL_MUTATION" }
// { type: "START_GARAGE_LIST" }
// { type: "REQUEST_FULL_LIST" }
// { type: "START_UNCERTAIN_QUANTITY" }
// { type: "CLARIFY_QUANTITY", quantity: 15 | 50 }
// { type: "START_EXCESSIVE_REMOVE" }
// { type: "INTERRUPT", utterance: string }
// { type: "RESET", channel?: "text" | "voice" }
```

- [ ] **Step 4: Implement the reducer**

Add the pure module below. Every branch must return new objects and must not query or modify the DOM.

```js
const ConversationPrototype = (() => {
  const paintIds = new Set(["white-paint", "blue-paint", "primer"]);

  const receive = (state, utterance) =>
    appendTurn(
      { ...state, status: ConversationStatus.UNDERSTANDING },
      "You",
      utterance
    );

  const applyAdd = (state, entryId, amount) => {
    const entry = state.inventory.entries.find((candidate) => candidate.id === entryId);
    const resultingQuantity = entry.quantity + amount;
    const nextState = {
      ...state,
      inventory: updateEntry(state.inventory, entryId, (current) => ({
        quantity: current.quantity + amount,
      })),
      status: ConversationStatus.APPLIED,
      pendingIntent: null,
      uncertainFields: [],
    };
    return withAgentResponse(
      nextState,
      Responses.added(state.channel, entry, amount, resultingQuantity),
      `Applied Add to ${entry.name}: ${entry.quantity} → ${resultingQuantity} ${entry.unit}.`
    );
  };

  const applyRemove = (state, entryId, amount) => {
    const entry = state.inventory.entries.find((candidate) => candidate.id === entryId);
    if (amount > entry.quantity) {
      return withAgentResponse(
        {
          ...state,
          status: ConversationStatus.FAILED,
          pendingIntent: null,
          candidates: [],
        },
        Responses.insufficient(entry, amount),
        "Rejected Remove; Inventory is unchanged because the requested amount exceeds On-hand Stock."
      );
    }

    const resultingQuantity = entry.quantity - amount;
    const nextState = {
      ...state,
      inventory: updateEntry(state.inventory, entryId, (current) => ({
        quantity: current.quantity - amount,
      })),
      status: ConversationStatus.APPLIED,
      pendingIntent: null,
      candidates: [],
    };
    return withAgentResponse(
      nextState,
      Responses.removed(state.channel, entry, amount, resultingQuantity),
      `Applied Remove to ${entry.name}: ${entry.quantity} → ${resultingQuantity} ${entry.unit}.`
    );
  };

  const reducer = (state, action) => {
    switch (action.type) {
      case "SET_CHANNEL":
        return {
          ...state,
          channel: action.channel,
          transition: `Changed presentation to ${action.channel}; mutation policy is unchanged.`,
        };

      case "RESET":
        return createInitialState(action.channel ?? state.channel);

      case "START_ADD": {
        const understood = receive(state, "Add 4 boxes of nitrile gloves to the garage.");
        return applyAdd(understood, "gloves", 4);
      }

      case "START_AMBIGUOUS_REMOVE": {
        const understood = receive(state, "Remove 2 batteries.");
        return withAgentResponse(
          {
            ...understood,
            status: ConversationStatus.CLARIFYING,
            pendingIntent: { kind: "remove", amount: 2 },
            candidates: ["aa-batteries", "aaa-batteries"],
          },
          Responses.ambiguousBatteries(state.channel),
          "Paused before mutation because the reference has two Matches."
        );
      }

      case "CHOOSE_MATCH": {
        if (state.status !== ConversationStatus.CLARIFYING || state.pendingIntent?.kind !== "remove") {
          return state;
        }
        const entry = state.inventory.entries.find((candidate) => candidate.id === action.entryId);
        const clarified = appendTurn(state, "You", entry.name);
        return applyRemove(clarified, action.entryId, state.pendingIntent.amount);
      }

      case "START_BULK_SET": {
        const understood = receive(state, "Set all garage paint to zero.");
        const proposedMutations = understood.inventory.entries
          .filter((entry) => paintIds.has(entry.id))
          .map((entry) => ({
            entryId: entry.id,
            name: entry.name,
            unit: entry.unit,
            before: entry.quantity,
            after: 0,
          }));
        return withAgentResponse(
          {
            ...understood,
            status: ConversationStatus.CONFIRMING,
            pendingIntent: { kind: "bulk-set-zero" },
            proposedMutations,
          },
          Responses.bulkPreview(state.channel, proposedMutations),
          "Prepared an exact atomic preview; Inventory is unchanged until explicit confirmation."
        );
      }

      case "CONFIRM_MUTATION": {
        if (state.status !== ConversationStatus.CONFIRMING || state.pendingIntent?.kind !== "bulk-set-zero") {
          return state;
        }
        const confirmed = appendTurn(state, "You", "Yes, apply it.");
        const ids = new Set(confirmed.proposedMutations.map((mutation) => mutation.entryId));
        const inventory = {
          ...confirmed.inventory,
          entries: confirmed.inventory.entries.map((entry) =>
            ids.has(entry.id) ? { ...entry, quantity: 0 } : entry
          ),
        };
        return withAgentResponse(
          {
            ...confirmed,
            inventory,
            status: ConversationStatus.APPLIED,
            pendingIntent: null,
            proposedMutations: [],
          },
          Responses.bulkApplied(state.channel, ids.size),
          `Atomically set ${ids.size} Stock Entries to zero.`
        );
      }

      case "CANCEL_MUTATION": {
        if (state.status !== ConversationStatus.CONFIRMING) {
          return state;
        }
        const cancelled = appendTurn(state, "You", "No, cancel.");
        return withAgentResponse(
          {
            ...cancelled,
            status: ConversationStatus.IDLE,
            pendingIntent: null,
            proposedMutations: [],
          },
          "Cancelled. Nothing changed.",
          "Cleared the proposed mutation; Inventory is unchanged."
        );
      }

      case "START_GARAGE_LIST": {
        const understood = receive(state, "What do I have in the garage?");
        const entries = understood.inventory.entries.filter(
          (entry) => entry.location === "Garage" && entry.quantity > 0
        );
        return withAgentResponse(
          {
            ...understood,
            status: ConversationStatus.APPLIED,
            pendingIntent: state.channel === "voice" ? { kind: "garage-list", entryIds: entries.map((entry) => entry.id) } : null,
          },
          Responses.garageList(state.channel, entries),
          state.channel === "voice"
            ? "Returned a spoken summary and preserved the result set for progressive disclosure."
            : "Returned the complete filtered List."
        );
      }

      case "REQUEST_FULL_LIST": {
        if (state.pendingIntent?.kind !== "garage-list") {
          return state;
        }
        const entries = state.pendingIntent.entryIds.map((entryId) =>
          state.inventory.entries.find((entry) => entry.id === entryId)
        );
        const requested = appendTurn(state, "You", "Read the full list.");
        return withAgentResponse(
          { ...requested, pendingIntent: null },
          entries.map((entry) => `${entry.name}, ${formatQuantity(entry.quantity, entry.unit)}`).join("; "),
          "Expanded the preserved List result without rerunning or changing Inventory."
        );
      }

      case "START_UNCERTAIN_QUANTITY": {
        const understood = receive(state, "Add [fifteen or fifty] boxes of air filters.");
        return withAgentResponse(
          {
            ...understood,
            status: ConversationStatus.CLARIFYING,
            pendingIntent: { kind: "add", entryId: "filters" },
            uncertainFields: ["quantity"],
          },
          Responses.uncertainQuantity(),
          "Paused before mutation because Quantity recognition is uncertain; the rest of the intent is preserved."
        );
      }

      case "CLARIFY_QUANTITY": {
        if (state.pendingIntent?.kind !== "add" || !state.uncertainFields.includes("quantity")) {
          return state;
        }
        const clarified = appendTurn(state, "You", `${action.quantity} boxes.`);
        return applyAdd(clarified, state.pendingIntent.entryId, action.quantity);
      }

      case "START_EXCESSIVE_REMOVE": {
        const understood = receive(state, "Remove 30 AA batteries.");
        return applyRemove(understood, "aa-batteries", 30);
      }

      case "INTERRUPT": {
        const interrupted = appendTurn(state, "You", action.utterance);
        return withAgentResponse(
          {
            ...interrupted,
            status: ConversationStatus.IDLE,
            pendingIntent: null,
            candidates: [],
            proposedMutations: [],
            uncertainFields: [],
          },
          "Okay. I cleared the unfinished change. What would you like to do instead?",
          "Discarded the stale pending mutation before accepting a new unrelated intent."
        );
      }

      default:
        return state;
    }
  };

  return { createInitialState, reducer };
})();
```

- [ ] **Step 5: Inspect the reducer for forbidden DOM coupling**

Run:

```bash
grep -nE 'document|querySelector|getElementById|innerHTML' prototypes/inventory-conversation-prototype.html
```

Expected: matches occur only in the page-shell code added in Task 3, not between `const ConversationPrototype =` and its closing `})();`. At this stage, before Task 3, no matches should occur in the script.

- [ ] **Step 6: Commit the reducer**

```bash
git add prototypes/inventory-conversation-prototype.html
git commit -m "prototype: model inventory conversations" \
  -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 3: Add Guided Walkthroughs and Free-Play Dispatch

**Files:**
- Modify: `prototypes/inventory-conversation-prototype.html`

- [ ] **Step 1: Define the four walkthroughs**

Add these definitions after the pure module. A walkthrough step is a real reducer action, not a special transcript-only operation.

```js
const walkthroughs = [
  {
    id: "clear-add",
    title: "Clear Add",
    description: "A single clear Match applies immediately and receives a concise result read-back.",
    steps: [
      { label: "Send clear Add", action: { type: "START_ADD" } },
    ],
  },
  {
    id: "ambiguous-remove",
    title: "Ambiguous Remove",
    description: "The agent pauses on multiple Matches and asks for the smallest useful distinction.",
    steps: [
      { label: "Ask to remove batteries", action: { type: "START_AMBIGUOUS_REMOVE" } },
      { label: "Choose AA batteries", action: { type: "CHOOSE_MATCH", entryId: "aa-batteries" } },
    ],
  },
  {
    id: "bulk-set",
    title: "Bulk Set",
    description: "Plural scope produces an exact atomic preview and requires explicit confirmation.",
    steps: [
      { label: "Ask to zero garage paint", action: { type: "START_BULK_SET" } },
      { label: "Confirm the preview", action: { type: "CONFIRM_MUTATION" } },
    ],
  },
  {
    id: "garage-list",
    title: "Garage List",
    description: "Text returns the complete List; voice summarizes and offers progressive disclosure.",
    steps: [
      { label: "Ask what is in the garage", action: { type: "START_GARAGE_LIST" } },
      { label: "Request full spoken List", voiceOnly: true, action: { type: "REQUEST_FULL_LIST" } },
    ],
  },
];
```

- [ ] **Step 2: Add page state and safe HTML formatting**

```js
let activeWalkthroughId = walkthroughs[0].id;
let completedStepCount = 0;
let state = ConversationPrototype.createInitialState("text");

const elements = {
  tabs: document.getElementById("scenario-tabs"),
  title: document.getElementById("scenario-title"),
  description: document.getElementById("scenario-description"),
  transcript: document.getElementById("transcript"),
  walkthroughControls: document.getElementById("walkthrough-controls"),
  conversationState: document.getElementById("conversation-state"),
  inventoryState: document.getElementById("inventory-state"),
  transition: document.getElementById("transition-explanation"),
  freePlayControls: document.getElementById("free-play-controls"),
  channelButtons: [...document.querySelectorAll("[data-channel]")],
};

const escapeHtml = (value) =>
  String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");

const activeWalkthrough = () =>
  walkthroughs.find((walkthrough) => walkthrough.id === activeWalkthroughId);

const dispatch = (action) => {
  state = ConversationPrototype.reducer(state, action);
  render();
};
```

- [ ] **Step 3: Render the transcript and state panels**

```js
const renderTranscript = () => {
  elements.transcript.innerHTML = state.transcript.length
    ? state.transcript
        .map(
          (turn) => `
            <li class="turn turn-${turn.speaker.toLowerCase()}">
              <strong>${escapeHtml(turn.speaker)}</strong>
              <p>${escapeHtml(turn.text)}</p>
            </li>
          `
        )
        .join("")
    : '<li class="empty-state">Press the next walkthrough step to begin.</li>';
};

const renderConversationState = () => {
  const pending = state.pendingIntent
    ? escapeHtml(JSON.stringify(state.pendingIntent))
    : "None";
  const candidates = state.candidates.length
    ? state.candidates
        .map((entryId) => state.inventory.entries.find((entry) => entry.id === entryId)?.name)
        .join(", ")
    : "None";
  const proposed = state.proposedMutations.length
    ? `${state.proposedMutations.length} exact change(s)`
    : "None";

  elements.conversationState.innerHTML = `
    <div><dt>Channel</dt><dd>${escapeHtml(state.channel)}</dd></div>
    <div><dt>Status</dt><dd>${escapeHtml(state.status)}</dd></div>
    <div><dt>Pending intent</dt><dd>${pending}</dd></div>
    <div><dt>Candidate Matches</dt><dd>${escapeHtml(candidates)}</dd></div>
    <div><dt>Proposed mutations</dt><dd>${escapeHtml(proposed)}</dd></div>
    <div><dt>Uncertain fields</dt><dd>${escapeHtml(state.uncertainFields.join(", ") || "None")}</dd></div>
  `;
};

const renderInventory = () => {
  elements.inventoryState.innerHTML = `
    <table>
      <thead>
        <tr><th>Stock Entry</th><th>Quantity</th><th>Location</th></tr>
      </thead>
      <tbody>
        ${state.inventory.entries
          .map(
            (entry) => `
              <tr class="${entry.quantity === 0 ? "zero-entry" : ""}">
                <td>${escapeHtml(entry.name)}</td>
                <td>${escapeHtml(formatQuantity(entry.quantity, entry.unit))}</td>
                <td>${escapeHtml(entry.location)}</td>
              </tr>
            `
          )
          .join("")}
      </tbody>
    </table>
  `;
};
```

- [ ] **Step 4: Render tabs and walkthrough controls**

```js
const renderTabs = () => {
  elements.tabs.innerHTML = walkthroughs
    .map(
      (walkthrough) => `
        <button
          type="button"
          data-scenario="${walkthrough.id}"
          aria-current="${walkthrough.id === activeWalkthroughId ? "page" : "false"}"
        >
          ${escapeHtml(walkthrough.title)}
        </button>
      `
    )
    .join("");

  elements.tabs.querySelectorAll("[data-scenario]").forEach((button) => {
    button.addEventListener("click", () => {
      activeWalkthroughId = button.dataset.scenario;
      completedStepCount = 0;
      state = ConversationPrototype.createInitialState(state.channel);
      render();
    });
  });
};

const renderWalkthroughControls = () => {
  const walkthrough = activeWalkthrough();
  const visibleSteps = walkthrough.steps.filter(
    (step) => !step.voiceOnly || state.channel === "voice"
  );
  const nextStep = visibleSteps[completedStepCount];

  elements.walkthroughControls.innerHTML = `
    <button type="button" id="reset-walkthrough" class="secondary">Reset walkthrough</button>
    ${
      nextStep
        ? `<button type="button" id="next-step">${escapeHtml(nextStep.label)}</button>`
        : '<span class="complete">Walkthrough complete — use free play or reset.</span>'
    }
  `;

  document.getElementById("reset-walkthrough").addEventListener("click", () => {
    completedStepCount = 0;
    state = ConversationPrototype.createInitialState(state.channel);
    render();
  });

  document.getElementById("next-step")?.addEventListener("click", () => {
    dispatch(nextStep.action);
    completedStepCount += 1;
    render();
  });
};
```

- [ ] **Step 5: Render free-play controls**

```js
const freePlayActions = [
  { label: "Ambiguous Remove", action: { type: "START_AMBIGUOUS_REMOVE" } },
  { label: "Choose AAA batteries", action: { type: "CHOOSE_MATCH", entryId: "aaa-batteries" } },
  { label: "Preview bulk Set", action: { type: "START_BULK_SET" } },
  { label: "Cancel pending mutation", action: { type: "CANCEL_MUTATION" } },
  { label: "Uncertain spoken Quantity", action: { type: "START_UNCERTAIN_QUANTITY" } },
  { label: "Clarify as 15 boxes", action: { type: "CLARIFY_QUANTITY", quantity: 15 } },
  { label: "Clarify as 50 boxes", action: { type: "CLARIFY_QUANTITY", quantity: 50 } },
  { label: "Remove too many batteries", action: { type: "START_EXCESSIVE_REMOVE" } },
  { label: "Interrupt pending turn", action: { type: "INTERRUPT", utterance: "Actually, never mind. What else is in the garage?" } },
];

const renderFreePlayControls = () => {
  elements.freePlayControls.innerHTML = freePlayActions
    .map(
      (item, index) =>
        `<button type="button" data-free-play="${index}" class="secondary">${escapeHtml(item.label)}</button>`
    )
    .join("");

  elements.freePlayControls.querySelectorAll("[data-free-play]").forEach((button) => {
    button.addEventListener("click", () => {
      dispatch(freePlayActions[Number(button.dataset.freePlay)].action);
    });
  });
};
```

- [ ] **Step 6: Add the top-level render and channel switch**

```js
const render = () => {
  const walkthrough = activeWalkthrough();
  elements.title.textContent = walkthrough.title;
  elements.description.textContent = walkthrough.description;
  elements.transition.textContent = state.transition;

  elements.channelButtons.forEach((button) => {
    button.setAttribute("aria-pressed", String(button.dataset.channel === state.channel));
  });

  renderTabs();
  renderTranscript();
  renderConversationState();
  renderInventory();
  renderWalkthroughControls();
  renderFreePlayControls();
};

elements.channelButtons.forEach((button) => {
  button.addEventListener("click", () => {
    dispatch({ type: "SET_CHANNEL", channel: button.dataset.channel });
  });
});

render();
```

- [ ] **Step 7: Manually drive each reducer path**

Open the file:

```bash
xdg-open prototypes/inventory-conversation-prototype.html
```

Expected:

- **Clear Add:** gloves change from 6 to 10 boxes without confirmation.
- **Ambiguous Remove:** Inventory does not change until AA batteries are chosen; AA batteries then change from 24 to 22.
- **Bulk Set:** all three paint entries remain unchanged on preview and become zero together only after confirmation.
- **Garage List:** text shows the full result immediately; voice shows a summary and enables the full-list step.
- **Free play:** uncertain Quantity preserves the Add until 15 or 50 is selected; excessive Remove fails without changing AA batteries; cancellation and interruption clear pending state.

- [ ] **Step 8: Commit walkthrough behavior**

```bash
git add prototypes/inventory-conversation-prototype.html
git commit -m "prototype: add guided inventory conversations" \
  -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 4: Style the Prototype for Readable Evaluation

**Files:**
- Modify: `prototypes/inventory-conversation-prototype.html`

- [ ] **Step 1: Replace the CSS marker with complete styles**

Replace the comment in the `<style>` element with:

```css
:root {
  color-scheme: light;
  --ink: #172033;
  --muted: #657087;
  --line: #dbe1ea;
  --surface: #ffffff;
  --surface-soft: #f5f7fb;
  --accent: #3157d5;
  --accent-soft: #e8edff;
  --success: #176b45;
  --danger-soft: #fff1f0;
  font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
}

* { box-sizing: border-box; }

body {
  margin: 0;
  background: #eef2f7;
  color: var(--ink);
}

button {
  border: 1px solid transparent;
  border-radius: 0.65rem;
  background: var(--accent);
  color: white;
  cursor: pointer;
  font: inherit;
  font-weight: 700;
  padding: 0.7rem 1rem;
}

button:hover { filter: brightness(0.96); }
button:focus-visible { outline: 3px solid #9db0ff; outline-offset: 2px; }
button.secondary { background: var(--surface); border-color: var(--line); color: var(--ink); }
button[aria-pressed="true"], button[aria-current="page"] { background: var(--accent-soft); border-color: var(--accent); color: var(--accent); }

.page {
  margin: 0 auto;
  max-width: 1440px;
  padding: 2.5rem;
}

.intro { max-width: 850px; }
.intro h1 { font-size: clamp(2rem, 4vw, 3.5rem); line-height: 1.05; margin: 0.35rem 0 1rem; }
.intro > p:last-child { color: var(--muted); font-size: 1.1rem; line-height: 1.65; }
.eyebrow { color: var(--accent); font-size: 0.75rem; font-weight: 800; letter-spacing: 0.11em; margin: 0 0 0.35rem; text-transform: uppercase; }
.muted { color: var(--muted); line-height: 1.55; }

.scenario-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 0.65rem;
  margin: 2rem 0 1rem;
}

.workspace {
  display: grid;
  gap: 1rem;
  grid-template-columns: minmax(0, 1.35fr) minmax(320px, 0.65fr);
}

.conversation-panel,
.state-panel,
.free-play {
  background: var(--surface);
  border: 1px solid var(--line);
  border-radius: 1rem;
  box-shadow: 0 12px 35px rgb(43 57 89 / 8%);
}

.conversation-panel { min-height: 680px; padding: 1.5rem; }
.panel-heading { align-items: flex-start; display: flex; gap: 1rem; justify-content: space-between; }
.panel-heading h2, .free-play h2 { margin: 0; }
.channel-switch { display: flex; gap: 0.4rem; }
.channel-switch button { background: var(--surface); border-color: var(--line); color: var(--ink); }

.transcript {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  list-style: none;
  margin: 1.5rem 0;
  min-height: 430px;
  padding: 0;
}

.turn {
  border: 1px solid var(--line);
  border-radius: 0.85rem;
  max-width: 85%;
  padding: 0.85rem 1rem;
}

.turn p { line-height: 1.5; margin: 0.25rem 0 0; }
.turn-you { align-self: flex-end; background: var(--accent); border-color: var(--accent); color: white; }
.turn-agent { align-self: flex-start; background: var(--surface-soft); }
.empty-state { color: var(--muted); margin: auto; text-align: center; }
.controls { align-items: center; display: flex; flex-wrap: wrap; gap: 0.65rem; }
.complete { color: var(--success); font-weight: 700; }

.state-column { display: flex; flex-direction: column; gap: 1rem; }
.state-panel { overflow: hidden; padding: 1.25rem; }
.state-panel dl { display: grid; gap: 0.65rem; margin: 0; }
.state-panel dl div { border-bottom: 1px solid var(--line); display: grid; gap: 0.3rem; grid-template-columns: 135px 1fr; padding-bottom: 0.65rem; }
.state-panel dt { color: var(--muted); font-size: 0.8rem; font-weight: 700; }
.state-panel dd { margin: 0; overflow-wrap: anywhere; }
.transition-panel { border-left: 4px solid var(--accent); }

table { border-collapse: collapse; font-size: 0.82rem; width: 100%; }
th, td { border-bottom: 1px solid var(--line); padding: 0.5rem 0.35rem; text-align: left; }
th { color: var(--muted); font-size: 0.72rem; text-transform: uppercase; }
.zero-entry { background: var(--danger-soft); color: var(--muted); }

.free-play { margin-top: 1rem; padding: 1.5rem; }
.free-play-grid { display: grid; gap: 0.65rem; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); margin-top: 1rem; }

@media (max-width: 960px) {
  .page { padding: 1.25rem; }
  .workspace { grid-template-columns: 1fr; }
  .conversation-panel { min-height: auto; }
  .transcript { min-height: 360px; }
}

@media (max-width: 600px) {
  .panel-heading { flex-direction: column; }
  .state-panel dl div { grid-template-columns: 1fr; }
  .turn { max-width: 95%; }
}
```

- [ ] **Step 2: Check keyboard and narrow-screen use**

Run:

```bash
xdg-open prototypes/inventory-conversation-prototype.html
```

Expected:

- Tab reaches every scenario, channel, walkthrough, and free-play button with a visible focus ring.
- At a browser width near 390 pixels, the conversation and state panels stack without horizontal page scrolling.
- Text and voice modes are visually distinguishable through the pressed channel button, not color alone.
- Zero-quantity Stock Entries remain visible and are visually de-emphasized rather than removed.

- [ ] **Step 3: Commit styling**

```bash
git add prototypes/inventory-conversation-prototype.html
git commit -m "prototype: style inventory conversation demo" \
  -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 5: Evaluate the Approved Scenarios and Capture the Artifact

**Files:**
- Modify: `prototypes/inventory-conversation-prototype.html` only if evaluation exposes a conversation-policy defect.
- Modify: `docs/superpowers/specs/2026-08-28-inventory-conversation-prototype-design.md`

- [ ] **Step 1: Run the required evaluation matrix**

Open:

```bash
xdg-open prototypes/inventory-conversation-prototype.html
```

Exercise this matrix in order:

| Scenario | Text expectation | Voice expectation | Inventory safety |
|---|---|---|---|
| Clear Add | Names Stock Entry, Location, added and resulting Quantity | Shorter result with same mutation | Gloves become 10 boxes |
| Ambiguous Remove | Lists minimal distinguishing attributes | Asks “AA … or AAA …?” | No change before choice; AA becomes 22 after choice |
| Bulk Set | Shows every before/after value | Summarizes names and shared result | No change before confirmation; all three become zero together |
| Garage List | Shows complete filtered List | Gives count and top three, then offers detail | Query never changes Inventory |
| Uncertain Quantity | Preserves intent until Quantity is supplied | Targeted “fifteen or fifty?” | No change before clarification |
| Excessive Remove | Reports available Quantity | Same policy in concise speech | AA remains unchanged |
| Cancellation | Clears exact preview | Same policy | Inventory unchanged |
| Interruption | Clears pending intent before unrelated request | Same policy | Stale mutation cannot later apply |

Expected: every row matches. If a row does not match, change only the reducer or response function responsible, repeat the full affected walkthrough in both channels, and commit the correction separately.

- [ ] **Step 2: Confirm the artifact is self-contained**

Run:

```bash
grep -nE '<script[^>]+src=|<link[^>]+href=|fetch\\(|XMLHttpRequest|localStorage|sessionStorage' prototypes/inventory-conversation-prototype.html
```

Expected: no output. The prototype has no external assets, network dependency, or persistence.

- [ ] **Step 3: Confirm the reducer is isolated from the DOM**

Run:

```bash
awk '/const ConversationPrototype =/{inside=1} inside{print} /return \\{ createInitialState, reducer \\};/{getline; print; inside=0}' prototypes/inventory-conversation-prototype.html \
  | grep -nE 'document|querySelector|getElementById|innerHTML'
```

Expected: no output.

- [ ] **Step 4: Update the capture wording**

In `docs/superpowers/specs/2026-08-28-inventory-conversation-prototype-design.md`, replace:

```markdown
The HTML prototype will be added beside this design document on the throwaway `prototype/inventory-conversation` branch.
```

with:

```markdown
The self-contained HTML prototype lives at `prototypes/inventory-conversation-prototype.html` on the throwaway `prototype/inventory-conversation` branch.
```

- [ ] **Step 5: Commit the evaluated artifact**

```bash
git add prototypes/inventory-conversation-prototype.html docs/superpowers/specs/2026-08-28-inventory-conversation-prototype-design.md
git commit -m "prototype: capture validated inventory conversations" \
  -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

- [ ] **Step 6: Push the throwaway branch**

```bash
git push -u origin prototype/inventory-conversation
```

Expected: GitHub reports the remote branch URL and the branch is available as the prototype's primary-source context pointer.

- [ ] **Step 7: Link the artifact from the Wayfinder ticket**

Post a comment on **Prototype the inventory mutation conversation in text and voice**:

```bash
gh issue comment 9 --repo JoranBergfeld/multi-channel-agent --body "$(cat <<'EOF'
Prototype artifact: [`prototypes/inventory-conversation-prototype.html`](https://github.com/JoranBergfeld/multi-channel-agent/blob/prototype/inventory-conversation/prototypes/inventory-conversation-prototype.html) on the throwaway `prototype/inventory-conversation` branch.

Please run the four guided walkthroughs in both text and voice modes, then exercise the free-play controls for uncertain Quantity, excessive Remove, cancellation, and interruption. The ticket is ready to resolve when those conversations no longer feel surprising, unsafe, inconsistent, or unnecessarily verbose.
EOF
)"
```

Expected: the issue comment contains a working link to the HTML source on the throwaway branch. Do not close the ticket yet; Wayfinder resolution requires the human's reaction to the runnable prototype.

### Task 6: Resolve the Wayfinder Decision After Human Evaluation

**Files:**
- Modify: `CONTEXT.md` only if evaluation resolves a new stable domain term; do not add implementation language.
- Modify GitHub issues **Prototype the inventory mutation conversation in text and voice** and **Map: Multi-channel inventory agent on Azure**.

- [ ] **Step 1: Collect the human verdict**

Have the user run the artifact and state whether any walkthrough or free-play transition feels:

- surprising or unsafe;
- inconsistent between text and voice;
- too verbose or too terse;
- wrong about confirmation, disambiguation, long Lists, or failure recovery.

Expected: either concrete revisions or explicit acceptance. Do not infer acceptance from the artifact merely running.

- [ ] **Step 2: Apply and recapture any requested policy revisions**

If revisions are requested:

1. Change the relevant reducer branch or `Responses` function.
2. Repeat the affected guided walkthrough in text and voice.
3. Repeat any free-play case sharing that transition.
4. Commit with:

```bash
git add prototypes/inventory-conversation-prototype.html
git commit -m "prototype: refine inventory conversation policy" \
  -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git push
```

Expected: the branch link continues to point at the latest accepted artifact.

- [ ] **Step 3: Record the resolution comment**

After explicit acceptance, post:

```bash
gh issue comment 9 --repo JoranBergfeld/multi-channel-agent --body "$(cat <<'EOF'
## Resolution

The prototype validated one shared, balanced conversation policy across text and voice:

- Clear, low-risk, single-entry mutations apply immediately with a result read-back.
- Ambiguous references require minimal distinguishing choices; the agent never silently selects a Match.
- Uncertain spoken Quantity or Unit triggers targeted clarification while preserving the pending intent.
- Bulk, destructive, and otherwise high-risk mutations require an exact preview and explicit confirmation; bulk changes apply atomically.
- Text may present complete detail immediately. Voice uses concise prompts and progressive disclosure, summarizing long Lists before offering full detail or narrowing.
- Failed, cancelled, or interrupted turns leave Inventory unchanged and clear any stale pending mutation.

Primary source: [`prototypes/inventory-conversation-prototype.html`](https://github.com/JoranBergfeld/multi-channel-agent/blob/prototype/inventory-conversation/prototypes/inventory-conversation-prototype.html).
EOF
)"
```

- [ ] **Step 4: Close the ticket**

```bash
gh issue close 9 --repo JoranBergfeld/multi-channel-agent
```

- [ ] **Step 5: Append the map context pointer**

Edit **Map: Multi-channel inventory agent on Azure** and append this line under `## Decisions so far`:

```markdown
- [Prototype the inventory mutation conversation in text and voice](https://github.com/JoranBergfeld/multi-channel-agent/issues/9): clear low-risk single-entry mutations apply immediately; ambiguity and uncertain speech trigger targeted clarification; high-risk and bulk mutations require exact confirmation; voice uses concise progressive disclosure while sharing the text channel's mutation policy.
```

Preserve every existing map section and decision line. Insert the decision immediately before `## Not yet specified`:

```bash
map_body="$(gh issue view 1 --repo JoranBergfeld/multi-channel-agent --json body --jq .body)"
decision='- [Prototype the inventory mutation conversation in text and voice](https://github.com/JoranBergfeld/multi-channel-agent/issues/9): clear low-risk single-entry mutations apply immediately; ambiguity and uncertain speech trigger targeted clarification; high-risk and bulk mutations require exact confirmation; voice uses concise progressive disclosure while sharing the text channel'\''s mutation policy.'
updated_body="$(
  jq -rn \
    --arg body "$map_body" \
    --arg decision "$decision" \
    '$body | sub("\n## Not yet specified"; "\n" + $decision + "\n\n## Not yet specified")'
)"
printf '%s\n' "$updated_body" \
  | gh issue edit 1 --repo JoranBergfeld/multi-channel-agent --body-file -
```

Expected: the closed ticket's decision appears once in the map as a linked one-line gist; detailed reasoning remains in the ticket resolution comment.
