# TraceWeave architecture

## 1. High-level pipeline

~~~text
Windows activity sources
  ├─ Mouse / keyboard shortcuts / committed text
  ├─ Clipboard
  ├─ Foreground window / process
  ├─ UI Automation / accessibility tree
  ├─ Screen frames / OCR
  ├─ Optional audio transcription
  └─ Explicitly selected file/system events
             |
             v
Capture adapters -> immutable event journal + evidence store
             |
             v
Timeline correlation and context assembly
             |
             v
First-pass workflow synthesis
  ├─ model-assisted intent and step extraction (when configured)
  └─ deterministic normalization and optimization
             |
             v
Workflow Draft v1
  ├─ manual editor
  └─ advanced-model review -> evidence-backed proposals
             |
             v
Versioned workflow -> validation -> approval -> export / execution
~~~

## 2. Architectural boundaries

### TraceWeave.Core
Platform-independent domain types and deterministic logic:
- capture event contracts;
- workflow and revision models;
- evidence references;
- first-pass synthesis and optimization contracts;
- review findings and correction proposals;
- versioned editing rules.

No Windows API, database, network, or UI dependencies belong here.

### TraceWeave.Infrastructure
Adapters and persistence:
- SQLite event/revision repository;
- Windows low-level input and clipboard adapters;
- UI Automation and foreground-window observers;
- screenshot/OCR providers;
- DPAPI-backed local protection for sensitive retained values;
- OpenAI-compatible and Anthropic model-provider adapters;
- export serializers and MCP server.

### TraceWeave.App
Native WPF user interface:
- visible recording controls and capture scope;
- event timeline and evidence viewer;
- workflow editor;
- first-pass optimization report;
- advanced AI review panel;
- version diff, restore, and settings.

## 3. Capture model

Capture is opt-in and session-scoped. A visible indicator must remain active while capture is running. The recorder should capture the smallest useful amount of information.

Keyboard handling is split into:
- shortcut/action events (for example Ctrl+C, Ctrl+V, Enter, Escape);
- committed text observed from accessible control state and IME-aware text-change events.

Raw key-by-key text reconstruction is not the default. Password controls, secure desktop, credential dialogs, and configured sensitive applications are excluded. Clipboard capture is separately configurable and can be paused independently.

Every event has a stable ID, recording ID, monotonic sequence, UTC timestamp, source, kind, process/window context, optional target metadata, sensitivity classification, and evidence links.

## 4. Immutable evidence and mutable interpretation

Raw evidence is append-only during retention. The application does not rewrite capture events to make a workflow look cleaner. A user-initiated privacy purge is an explicit exception and must be auditable.

Workflow documents are derived objects. Each revision is a complete immutable snapshot with:
- version and parent revision;
- author (human, first-pass engine, or named AI model);
- summary and structured change list;
- step-level evidence references;
- confidence and review state;
- model/prompt provenance when applicable.

Manual edits and accepted AI proposals create new revisions. Undo creates a restore revision; it does not mutate historical revisions.

## 5. First-pass generation

First-pass generation is a mandatory stage, not an optional polish command.

1. Normalize and order events.
2. Correlate clipboard copy/paste, focus, text changes, screenshots, and window transitions.
3. Segment the recording into task phases.
4. Infer candidate intent and action steps.
5. Remove only well-supported accidental/no-op events.
6. Merge duplicates only when target, timing, and intervening state evidence support it.
7. Replace fixed sleeps with observable state conditions when evidence permits.
8. Parameterize variable or sensitive values.
9. Validate that each generated step has evidence or is explicitly marked as an inference.
10. Produce an optimization report explaining every removal, merge, inference, and unresolved ambiguity.

When no model is configured, a deterministic synthesizer provides a conservative baseline. Model-assisted synthesis must still pass deterministic evidence and schema validation.

## 6. Advanced review

Advanced review is a separate, user-invoked pass over a selected workflow revision and its supporting evidence. It should identify:
- wrong or fragile ordering;
- missing preconditions or postconditions;
- redundant or ineffective steps missed by first pass;
- missing steps inferred from state transitions;
- contradictory values or branches;
- invalid selectors and weak verification;
- opportunities to parameterize, simplify, or use semantic controls.

The model returns findings and proposed patches, not a replacement that silently overwrites the source. Each proposal cites evidence IDs, gives a reason and confidence, and can be accepted, rejected, or edited by the user.

## 7. Execution boundary

The workflow engine should prefer semantic selectors and postcondition checks. Coordinates are a fallback with confidence and freshness checks. Destructive, external, financial, account, or security-sensitive actions require explicit approval. A failed postcondition stops the flow and enters a recovery/reporting state; it must not blindly continue.
