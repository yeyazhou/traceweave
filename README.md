# TraceWeave

**Capture what you do. Teach AI to do it again.**

TraceWeave is a Windows-first, local-first activity recorder that turns real desktop work into editable, reviewable, reusable AI workflows.

> **Status: v0.1.2 initial usable version.** Recording, first-pass optimization, manual revision, second-pass review, and workflow export are implemented. v0.1.2 fixes the UI Automation startup stall by removing global focus-event registration from the recording startup path, starting the dedicated MTA worker before any UIA provider call, and keeping UIA observation polling-based. Safe workflow execution/playback, OCR/screenshot evidence, and richer system/file event adapters remain the next phase.

## What v0.1.2 can do

### 1. Multi-source Windows capture

During an explicit visible recording session TraceWeave can capture:

- Mouse click / double-click / drag / scroll.
- Keyboard shortcuts and semantic special keys without storing ordinary characters key-by-key.
- Foreground application/window changes.
- UI Automation focus, element name, AutomationId, control type and committed text/value changes.
- Clipboard update events and paste correlation.
- Optional clipboard text capture with local redaction.
- Password controls and configured sensitive applications are excluded.

The implementation uses Windows low-level input hooks, foreground-window events, the Windows clipboard listener, and UI Automation. Low-level hook callbacks are kept lightweight and passed into the local event pipeline; Windows documents the timeout sensitivity of low-level hooks and the fact that asynchronous key state should not be queried from the callback itself. TraceWeave therefore tracks modifier state from the observed key stream rather than calling GetAsyncKeyState there.

## 2. First-pass optimization happens immediately

The first generated workflow is **not** a transcript of the recording.

The first pass currently:

- Ignores cursor movement noise.
- Removes unanchored waits.
- Merges repeated clicks on the same semantic target within a short evidence window.
- Merges adjacent repeated application switches.
- Correlates copy → paste operations into a data-transfer step.
- Removes an unconsumed terminal clipboard copy when the evidence strongly suggests it is incidental.
- Keeps uncertainty and provenance attached to the resulting step.
- Produces a report showing input events, candidate steps, final steps, removed noise, merged duplicates, and clipboard correlations.

This is deliberately conservative: when the evidence is insufficient to know whether an action was wrong, TraceWeave keeps it and marks it for review rather than silently deleting it.

## 3. Manual correction is versioned

Every edit creates a new workflow revision.

Supported in v0.1.x:

- Edit step title.
- Edit intent.
- Edit input template.
- Edit shortcut.
- Delete a step.
- Move a step up/down.
- Restore an earlier revision.
- Keep all earlier revisions immutable.

The workflow editor therefore behaves more like Git than like a destructive macro editor.

## 4. Second-pass AI correction

There are two review providers:

### Local reviewer

Works completely offline and detects:

- Weak selectors.
- Redundant adjacent actions.
- Unanchored waits.
- Missing postconditions.
- Missing final verification.
- Unconsumed clipboard operations.

It returns findings plus explicit proposals. Nothing is changed automatically.

### OpenAI-compatible reviewer

Configure an endpoint, model and API key in the AI Review tab.

The remote provider receives the **derived workflow revision**, not the raw event journal. Its response is validated against known step/evidence IDs before proposals can be applied.

Supported proposal types:

- Remove step.
- Move step.
- Replace step.
- Insert step.

Only selected proposals are applied, and applying them creates a new AiReview revision.

## 5. Export

A reviewed workflow can be exported to:

- *.workflow.json — complete structured workflow/revision data.
- *.SKILL.md — human/AI-readable execution guidance with semantic targets, preconditions, postconditions, confidence and rationale.

## Privacy model

TraceWeave is local-first by design.

- Raw capture events are persisted locally in SQLite.
- Password controls are never read as text.
- Built-in credential/password-manager windows are excluded.
- Extra excluded processes can be configured in Settings.
- Ordinary character key presses are not persisted one-by-one.
- Clipboard event metadata is captured independently from clipboard text.
- Clipboard text capture is opt-in.
- Secret-like values such as private keys, bearer tokens, common API-key formats and password=... patterns are blocked locally before persistence.
- Remote AI review does not receive raw capture events by default.

## Architecture

~~~text
Windows
  ├─ Low-level mouse / keyboard hooks
  ├─ Foreground window observer
  ├─ Clipboard listener
  └─ UI Automation
          ↓
    CaptureEvent journal
          ↓
   First-pass synthesis
          ↓
   Deterministic optimizer
          ↓
     Workflow Draft v1
       ↙           ↘
 Manual editor    AI reviewer
       ↘           ↙
        New revision
             ↓
      JSON / SKILL.md
~~~

Repository layout:

- src/TraceWeave.Core — domain models, synthesis, optimization, review contracts and revision logic.
- src/TraceWeave.Infrastructure — SQLite persistence, Windows capture adapters, AI provider and export.
- src/TraceWeave.App — native WPF desktop application.
- tests/TraceWeave.Core.Tests — deterministic tests.
- docs — architecture, lifecycle, security and roadmap.
- scripts — local launch and release helpers.

## Build

The project targets .NET 10.

On this workstation the .NET 10 SDK is installed under the user-local SDK path, so use:

~~~powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
& $dotnet restore .\TraceWeave.sln
& $dotnet build .\TraceWeave.sln -c Debug
& $dotnet test .\TraceWeave.sln -c Debug
~~~

Current test suite: **15 passing**.

## Run

For a development run:

~~~powershell
.\scripts\run-traceweave.ps1
~~~

The script explicitly points to the user-local .NET SDK so the app does not depend on the machine-wide C:\Program Files\dotnet runtime.

For a distributable Windows x64 build:

~~~powershell
.\scripts\publish-win-x64.ps1
~~~

The release helper creates a self-contained win-x64 single-file application under artifacts\TraceWeave-win-x64\.

## Local data

The application stores its local SQLite journal under:

~~~text
%LOCALAPPDATA%\TraceWeave\traceweave.db
~~~

No cloud service is required for recording, first-pass optimization, manual editing, local review or export.

## Current boundary

v0.1 intentionally stops before automatic playback/execution.

The next major layer is a **safe execution engine** that consumes the reviewed workflow and uses semantic UI Automation targets, preconditions, postconditions, approvals and recovery rather than blindly replaying mouse coordinates.

OCR/screenshot evidence will also be added as another evidence source rather than replacing the semantic capture pipeline.
