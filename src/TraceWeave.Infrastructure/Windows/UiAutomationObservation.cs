using TraceWeave.Core.Capture;

namespace TraceWeave.Infrastructure.Windows;

public sealed record UiAutomationTargetSnapshot(
    WindowContext Window,
    string? ElementName,
    string? AutomationId,
    string? ControlType,
    bool IsPassword);

public sealed record UiAutomationObservation(
    CaptureEventKind Kind,
    UiAutomationTargetSnapshot Target,
    string? TextValue = null,
    TextCaptureDisposition TextDisposition = TextCaptureDisposition.NotApplicable,
    SensitivityLevel Sensitivity = SensitivityLevel.Public,
    string? RedactionReason = null);
