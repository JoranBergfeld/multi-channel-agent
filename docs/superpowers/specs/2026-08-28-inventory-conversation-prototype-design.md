# Inventory Conversation Prototype Design

## Question

What should mutating and querying an Inventory feel like in text and voice, especially when an intent is ambiguous, risky, long, or based on uncertain speech recognition?

The prototype will test whether one shared, balanced conversation policy can remain predictable across channels while adapting how much detail each channel presents.

## Scope

The prototype covers four guided conversations:

1. A clear, low-risk Add.
2. An ambiguous Remove.
3. A bulk Set that requires confirmation.
4. A List of On-hand Stock in a Location.

Free-play controls will also exercise uncertain spoken Quantities or Units, insufficient On-hand Stock, cancellation, and interrupted clarification.

The prototype does not cover persistence, identity, Azure integration, production channel adapters, visual product design, automated tests, Undo, mutation history, or Unit conversion.

## Conversation Policy

The agent uses a balanced, risk-based policy:

- Apply a clear, low-risk, single-entry mutation immediately and read back the result.
- Clarify whenever a conversational reference yields more than one Match.
- Clarify an uncertain spoken Quantity or Unit before applying a mutation.
- Preview and explicitly confirm bulk, destructive, or otherwise high-risk mutations.
- Leave the Inventory unchanged when clarification fails, confirmation is declined, a Remove exceeds On-hand Stock, or a mutation cannot be applied atomically.

Text and voice share these decisions. Their presentation differs:

- Text presents complete details compactly when useful.
- Voice uses shorter responses, minimal distinguishing choices, and progressive disclosure.
- A long spoken List starts with the number of matching Stock Entries and a short useful summary, then offers the complete List or a narrower query.
- Speech recognition uncertainty blocks only on the uncertain field; the pending intent remains available so the user does not need to repeat the whole request.

## Prototype Shape

The artifact is one self-contained HTML file that opens directly in a browser. It contains:

1. A visible statement of the question and the policy being tested.
2. A readable view of the complete relevant conversation and Inventory state.
3. Four tabbed guided walkthroughs.
4. Free-play controls for channel, ambiguity, recognition confidence, confirmation, cancellation, interruption, and invalid amounts.
5. A plain-language explanation of the latest transition after every action.

Each guided walkthrough resets to a known Inventory and conversation state. Every walkthrough step is a real action button routed through the same conversation logic as free play.

## Portable Logic

The conversation logic is a pure reducer:

```text
reduceConversation(state, action) -> nextState
```

The state contains:

- The Inventory's Stock Entries and Locations needed by the scenarios.
- The active channel: text or voice.
- The conversation status: understanding, clarifying, confirming, applied, failed, or idle.
- The pending intent and any unresolved fields.
- Candidate Matches for an ambiguous reference.
- The exact proposed mutations awaiting confirmation.
- Recognition confidence and uncertain fields for voice input.
- The most recent channel-adapted response and a plain-language description of the state change.

Actions include receiving a scenario utterance, choosing a Match, clarifying a Quantity or Unit, confirming or cancelling a proposed mutation, requesting more List detail, interrupting a pending turn, and resetting a walkthrough.

The reducer does not access the DOM. The HTML page dispatches actions and renders the returned state.

## Legal Transitions

A received utterance is interpreted into an intent, target, Quantity, Unit, Location, scope, and recognition confidence.

From there:

- A complete, unambiguous, low-risk intent may transition directly to applied.
- An ambiguous Match or uncertain required field transitions to clarifying.
- A high-risk intent with an exact proposed effect transitions to confirming.
- Confirmation transitions to applied; cancellation transitions to idle without changing Inventory.
- An invalid mutation transitions to failed without changing Inventory.
- A successful clarification resumes evaluation of the preserved pending intent rather than starting a new intent.
- An interruption clears the pending mutation before accepting an unrelated intent.

Bulk mutations apply atomically or not at all.

## Guided Scenarios

### Clear Add

The user adds four boxes of nitrile gloves to the garage. The reference, Quantity, Unit, and Location are clear, so the mutation applies immediately. Text names the Stock Entry, Location, added Quantity, and resulting Quantity. Voice gives a shorter read-back with the same result.

### Ambiguous Remove

The user asks to remove two batteries when both AA batteries in the garage and AAA batteries in the utility room Match. The agent presents only the attributes needed to distinguish the candidates and never selects silently. After the user chooses AA batteries, the Remove applies and the result is read back.

### Bulk Set

The user asks to set all garage paint to zero. The agent previews every affected Stock Entry and its before-and-after Quantity, then requires explicit confirmation because the request has plural scope and Undo is out of scope. Voice summarizes the affected entries and effect; the exact preview remains visible in the prototype.

### Garage List

The user asks what is in the garage. Text shows the complete filtered List. Voice reports the number of matching on-hand Stock Entries, gives a short useful summary, then offers the full List or a narrower query.

## Failure and Recovery

- **No Match:** explain what was not found and preserve useful filters for the next turn.
- **Ambiguous Match:** present minimal distinguishing choices.
- **Uncertain spoken Quantity or Unit:** ask only for that field and preserve the pending intent.
- **Remove exceeds On-hand Stock:** reject it, report the available Quantity, and offer a valid amount.
- **Declined confirmation:** clear the proposed mutation without changing Inventory.
- **Interrupted clarification or confirmation:** clear the pending mutation before processing a new unrelated intent.
- **Bulk failure:** apply none of the proposed mutations.

The prototype never silently guesses a Match, invents Unit conversion, partially applies a bulk request, or applies a stale pending mutation.

## Evaluation

There are no automated tests. The prototype is deliberately throwaway and has no framework, persistence, network dependency, or production error handling.

The prototype answers its question when the user can complete the four guided walkthroughs and explore the free-play cases without encountering a mutation that feels surprising, unsafe, inconsistent between text and voice, or unnecessarily verbose. Any such reaction changes the conversation policy before the Wayfinder ticket is resolved.

## Capture

The HTML prototype will be added beside this design document on the throwaway `prototype/inventory-conversation` branch. The Wayfinder ticket will link to the branch as the primary source. Once the prototype has been evaluated, the ticket's resolution comment records the validated conversation decisions; production implementation remains out of scope for this map session.
