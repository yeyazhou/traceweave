# Security and privacy requirements

## Defaults

- Capture is opt-in and bounded to a visible recording session.
- Raw data stays local unless the user explicitly requests an AI operation that needs selected evidence.
- The recording indicator must be obvious and provide pause/stop controls.
- Clipboard capture is independently switchable.
- The user can exclude applications, windows, processes, and time ranges.
- Sensitive data is redacted before it is placed in prompts or exports.

## Never capture by default

- Password and password-confirmation controls (IsPassword and equivalent secure controls).
- Windows secure desktop, UAC credential prompts, lock screen, and credential-manager windows.
- Configured password managers, banking/payment apps, and other user-excluded applications.
- Authentication tokens, cookies, private keys, recovery codes, and secret-like strings.

## Text and clipboard handling

- Capture committed text from accessible control state where possible; do not reconstruct every character from a global keyboard hook by default.
- Record shortcut identity separately from text content.
- Mark clipboard content with sensitivity and capture disposition.
- Support metadata-only clipboard events when content capture is disabled.
- Encrypt retained sensitive payloads with a user-bound Windows protection mechanism.
- Do not include secrets in logs, crash reports, telemetry, workflow examples, or model prompts.

## AI boundary

- AI review is explicit and shows which evidence categories will be sent.
- Redaction runs locally before network transmission.
- Provider, model, endpoint, and data categories are visible.
- Do not send screenshots or clipboard contents merely because a provider is configured.
- AI outputs are untrusted proposals and must be schema-validated and evidence-checked.

## Retention and deletion

Raw evidence is append-only during its retention period. Users can explicitly purge a recording or selected evidence. Purge must remove associated local blobs and database references; immutable means no silent rewriting, not that the user loses deletion rights.
