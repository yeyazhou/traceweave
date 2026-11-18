namespace TraceWeave.Core.Capture;

public interface ICaptureEventJournal
{
    Task AppendAsync(CaptureEvent captureEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaptureEvent>> ReadRecordingAsync(
        Guid recordingId,
        CancellationToken cancellationToken = default);
}
