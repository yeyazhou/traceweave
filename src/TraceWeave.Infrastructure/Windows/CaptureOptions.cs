namespace TraceWeave.Infrastructure.Windows;

public sealed record CaptureOptions
{
    public bool CaptureMouse { get; init; } = true;
    public bool CaptureKeyboardShortcuts { get; init; } = true;
    public bool CaptureClipboardMetadata { get; init; } = true;
    public bool CaptureClipboardText { get; init; }
    public bool CaptureWindowChanges { get; init; } = true;
    public bool CaptureUiAutomation { get; init; } = true;
    public bool CaptureCommittedText { get; init; } = true;

    public IReadOnlySet<string> ExcludedProcessNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
