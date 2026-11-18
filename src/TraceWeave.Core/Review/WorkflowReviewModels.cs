using System.Collections.Immutable;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Core.Review;

public enum WorkflowFindingKind
{
    IncorrectOrder,
    MissingStep,
    RedundantStep,
    InvalidValue,
    Contradiction,
    UnsupportedInference,
    WeakVerification,
    FragileSelector,
    MissingPrecondition,
    MissingPostcondition,
    SecurityRisk,
    SimplificationOpportunity
}

public enum FindingSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

public enum ProposedChangeKind
{
    RemoveStep,
    ReplaceStep,
    InsertStep,
    MoveStep
}

public sealed record WorkflowReviewFinding(
    Guid Id,
    WorkflowFindingKind Kind,
    FindingSeverity Severity,
    string Title,
    string Explanation,
    double Confidence,
    ImmutableArray<Guid> EvidenceEventIds,
    Guid? RelatedStepId = null);

public sealed record WorkflowChangeProposal(
    Guid Id,
    ProposedChangeKind Kind,
    Guid? TargetStepId,
    WorkflowStep? ReplacementStep,
    WorkflowStep? InsertedStep,
    int? NewOrder,
    string Reason,
    double Confidence,
    ImmutableArray<Guid> EvidenceEventIds);

public sealed record WorkflowReviewRequest(
    WorkflowDocument Workflow,
    Guid RevisionId,
    string? AdditionalInstruction,
    bool IncludeScreenshots,
    bool IncludeClipboardEvidence);

public sealed record WorkflowReviewResult(
    Guid WorkflowId,
    Guid ReviewedRevisionId,
    string Provider,
    string ModelId,
    DateTimeOffset ReviewedAtUtc,
    string Summary,
    ImmutableArray<WorkflowReviewFinding> Findings,
    ImmutableArray<WorkflowChangeProposal> Proposals);

public interface IWorkflowReviewProvider
{
    Task<WorkflowReviewResult> ReviewAsync(
        WorkflowReviewRequest request,
        CancellationToken cancellationToken = default);
}
