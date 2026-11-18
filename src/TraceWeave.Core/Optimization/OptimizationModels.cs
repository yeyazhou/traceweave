using System.Collections.Immutable;
using TraceWeave.Core.Capture;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Core.Optimization;

public enum OptimizationSeverity
{
    Information,
    Warning,
    NeedsHumanReview
}

public sealed record OptimizationFinding(
    string Code,
    OptimizationSeverity Severity,
    string Message,
    ImmutableArray<Guid> EvidenceEventIds,
    double Confidence);

public sealed record WorkflowSynthesisRequest(
    Guid RecordingId,
    string? UserIntent,
    ImmutableArray<CaptureEvent> Events);

public sealed record WorkflowSynthesisDraft(
    string Name,
    string Goal,
    ImmutableArray<WorkflowParameter> Parameters,
    ImmutableArray<WorkflowStep> Steps,
    ImmutableArray<OptimizationFinding> Findings);

public sealed record FirstPassOptimizationReport(
    int InputEventCount,
    int CandidateStepCount,
    int OutputStepCount,
    int NoiseEventsRemoved,
    int DuplicateStepsMerged,
    int ClipboardTransfersCorrelated,
    ImmutableArray<OptimizationFinding> Findings);

public sealed record FirstPassGenerationResult(
    WorkflowDocument Workflow,
    FirstPassOptimizationReport Report);

public interface IWorkflowSynthesisProvider
{
    Task<WorkflowSynthesisDraft> SynthesizeAsync(
        WorkflowSynthesisRequest request,
        CancellationToken cancellationToken = default);
}
