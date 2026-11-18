# Workflow lifecycle and correction model

## Two distinct correction passes

### Pass 1 — automatic cleanup during initial generation

Runs every time a recording is converted into a workflow.

Goals:
- remove accidental cursor movement and irrelevant UI noise;
- discard duplicate clicks only when there is evidence they are redundant;
- merge fragmented text input when it belongs to the same control and entry;
- associate clipboard changes with later paste actions;
- collapse unnecessary navigation loops;
- replace arbitrary delays with state-based waits when the target state is observable;
- distinguish required actions from incidental actions;
- parameterize values that vary between runs;
- add preconditions and postconditions supported by evidence;
- flag ambiguous or weakly supported steps instead of inventing certainty.

Output: an optimized Draft v1 plus a report of removed, merged, inferred, parameterized, and unresolved items.

### Pass 2 — advanced review on demand

Runs when the user asks for deeper correction, or chooses a higher-capability model.

The reviewer receives:
- the selected workflow revision;
- relevant raw event summaries;
- screenshots and UIA evidence selected by the user/privacy policy;
- existing optimization findings;
- declared task goal and execution constraints.

It returns:
- findings classified by severity and category;
- evidence references for each finding;
- one or more proposed changes;
- confidence and uncertainty;
- expected impact and verification suggestions.

The reviewer never edits raw evidence or silently changes the current revision.

## Manual editing

The editor must support:
- add, delete, duplicate, reorder, and merge steps;
- edit title, intent, target, action, input binding, and variables;
- edit preconditions, postconditions, branches, and recovery behavior;
- mark steps required, optional, or conditional;
- attach or detach supporting evidence;
- accept/reject AI proposals individually;
- compare any two revisions;
- undo/restore by creating a new revision.

## Revision rules

- Revisions are immutable snapshots.
- Every revision has a parent revision except the initial revision.
- Every change records author, time, reason, and affected step IDs.
- AI changes must additionally record provider/model and prompt/schema version.
- A restore is a new revision based on an earlier snapshot.
- A workflow cannot be marked verified if required steps lack postconditions or unresolved high-severity findings remain.

## Evidence discipline

Each step is one of:
- **Observed** — directly supported by one or more capture events.
- **Correlated** — inferred by linking multiple observed events.
- **Suggested** — proposed by an AI reviewer but not yet accepted.
- **Human-authored** — manually added or changed.

A step must not be presented as observed if it is only suggested. Inference confidence is not a substitute for evidence.
