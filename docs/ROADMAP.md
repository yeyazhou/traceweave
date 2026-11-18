# Roadmap

## Phase 0 — foundation
- [x] Create Windows workstation project and Git repository wiring.
- [x] Select .NET 10 LTS and native WPF direction.
- [ ] Define core event, workflow, evidence, and revision contracts.
- [ ] Define first-pass and advanced-review boundaries.
- [ ] Add build/test baseline and architecture documentation.

## Phase 1 — workflow domain and editor
- [ ] Deterministic event-to-step synthesis.
- [ ] First-pass cleanup report and evidence links.
- [ ] Immutable workflow revisions.
- [ ] Manual step editing and revision diff.
- [ ] AI review findings/proposal contracts.
- [ ] Unit tests for deduplication, clipboard correlation, and version history.

## Phase 2 — Windows recorder MVP
- [ ] Visible recording session lifecycle.
- [ ] Foreground window and process tracking.
- [ ] Mouse click/scroll/drag events.
- [ ] Shortcut capture.
- [ ] Clipboard listener with per-session consent.
- [ ] UI Automation focus/value/text-change observation.
- [ ] IME committed-text tests.
- [ ] Screenshot evidence and OCR.
- [ ] Sensitive-control and application exclusions.

## Phase 3 — local persistence and timeline
- [ ] SQLite append-only event journal.
- [ ] Evidence blob store and cleanup.
- [ ] DPAPI-protected sensitive payloads.
- [ ] Searchable timeline and event/evidence inspector.
- [ ] Recording export/import.

## Phase 4 — model-assisted generation and review
- [ ] Provider-neutral AI contracts.
- [ ] OpenAI-compatible provider.
- [ ] Anthropic provider.
- [ ] First-pass synthesis with evidence-grounding validation.
- [ ] Advanced review with finding/proposal UI.
- [ ] Selective proposal application and version diff.
- [ ] Prompt/model provenance and cost controls.

## Phase 5 — execution and interoperability
- [ ] Semantic UIA execution.
- [ ] Browser and Computer adapters.
- [ ] Precondition/postcondition verification.
- [ ] Approval gates and recovery.
- [ ] SKILL.md and workflow.json export.
- [ ] MCP server and WebCodex integration.

## Release criteria

No release may claim reliable workflow replay until capture, first-pass cleanup, revision history, privacy exclusions, and postcondition verification have automated tests on supported Windows versions.
