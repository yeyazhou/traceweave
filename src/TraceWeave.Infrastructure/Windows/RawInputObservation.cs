using TraceWeave.Core.Capture;

namespace TraceWeave.Infrastructure.Windows;

public sealed record RawInputObservation(
    CaptureEventKind Kind,
    CaptureSource Source,
    string? Shortcut = null,
    string? Button = null,
    int X = 0,
    int Y = 0,
    int StartX = 0,
    int StartY = 0,
    int Delta = 0);
