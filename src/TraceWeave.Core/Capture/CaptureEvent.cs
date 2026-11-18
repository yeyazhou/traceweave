using System.Collections.Immutable;

namespace TraceWeave.Core.Capture;

public enum CaptureEventKind
{
    MouseClick,
    MouseDoubleClick,
    MouseDrag,
    MouseScroll,
    KeyChord,
    TextCommitted,
    ClipboardCopied,
    ClipboardPasted,
    WindowActivated,
    UiElementFocused,
    UiElementValueChanged,
    DelayObserved,
    ScreenshotCaptured,
    OcrTextDetected,
    FileCreated,
    FileChanged,
    FileRenamed,
    ProcessStarted,
    ProcessExited,
    RecordingMarker
}

public enum CaptureSource
{
    MouseHook,
    KeyboardHook,
    ClipboardListener,
    UiAutomation,
    WindowWatcher,
    ScreenCapture,
    Ocr,
    FileSystemWatcher,
    AudioTranscription,
    Manual
}

public enum TextCaptureDisposition
{
    NotApplicable,
    Captured,
    Redacted,
    Suppressed,
    Unavailable
}

public enum SensitivityLevel
{
    Public,
    Personal,
    Confidential,
    Secret
}

/// <summary>
/// Immutable observation from a single explicit recording session.
/// TextValue must already have passed local sensitive-control and redaction checks.
/// </summary>
public sealed record CaptureEvent(
    Guid Id,
    Guid RecordingId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    CaptureEventKind Kind,
    CaptureSource Source,
    string? ProcessName = null,
    string? WindowTitle = null,
    string? ElementName = null,
    string? AutomationId = null,
    string? ControlType = null,
    string? TextValue = null,
    TextCaptureDisposition TextDisposition = TextCaptureDisposition.NotApplicable,
    string? ClipboardCorrelationId = null,
    string? EvidencePath = null,
    ImmutableDictionary<string, string>? Metadata = null,
    SensitivityLevel Sensitivity = SensitivityLevel.Public,
    bool IsSynthetic = false)
{
    public string TargetKey =>
        !string.IsNullOrWhiteSpace(AutomationId) ? $"uia:{AutomationId}" :
        !string.IsNullOrWhiteSpace(ElementName) ? $"name:{ElementName}|{ControlType}" :
        !string.IsNullOrWhiteSpace(WindowTitle) ? $"window:{ProcessName}|{WindowTitle}" :
        $"process:{ProcessName}";

    public string? GetMetadata(string key) =>
        Metadata is not null && Metadata.TryGetValue(key, out var value) ? value : null;
}
