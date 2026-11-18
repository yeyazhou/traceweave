using System.Collections.Immutable;
using TraceWeave.Core.Capture;

namespace TraceWeave.Core.Workflows;

public enum WorkflowActionKind
{
    Click,
    DoubleClick,
    Drag,
    Scroll,
    PressShortcut,
    EnterText,
    CopyText,
    PasteText,
    TransferText,
    SwitchApplication,
    WaitForCondition,
    CreateFile,
    ChangeFile,
    RenameFile,
    VerifyState,
    Other
}

public enum StepOrigin
{
    Observed,
    Correlated,
    FirstPassSynthesized,
    AiProposed,
    HumanAuthored
}

public enum StepReviewStatus
{
    Unreviewed,
    Accepted,
    NeedsReview,
    Rejected
}

public enum WorkflowRevisionKind
{
    FirstPass,
    HumanEdit,
    AiReview,
    Restore,
    Import
}

public enum RevisionChangeKind
{
    Added,
    Removed,
    Modified,
    Reordered,
    Merged,
    NoiseRemoved,
    Simplified,
    Parameterized,
    WaitConditionInferred,
    Flagged,
    Restored
}

public sealed record WorkflowTarget(
    string? Application = null,
    string? WindowTitlePattern = null,
    string? AutomationId = null,
    string? ElementName = null,
    string? ControlType = null,
    string? FallbackDescription = null);

public sealed record EvidenceReference(
    Guid CaptureEventId,
    string Role,
    DateTimeOffset OccurredAtUtc,
    string? Summary = null);

public sealed record WorkflowParameter(
    string Name,
    string Description,
    bool IsRequired = true,
    SensitivityLevel Sensitivity = SensitivityLevel.Public,
    string? ExampleValue = null);

public sealed record WorkflowStep(
    Guid Id,
    int Order,
    string Title,
    string Intent,
    WorkflowActionKind Action,
    WorkflowTarget? Target,
    string? InputTemplate,
    string? Shortcut,
    ImmutableArray<string> Preconditions,
    ImmutableArray<string> Postconditions,
    ImmutableArray<EvidenceReference> Evidence,
    bool IsRequired,
    StepOrigin Origin,
    StepReviewStatus ReviewStatus,
    double Confidence,
    string? Rationale = null);

public sealed record RevisionChange(
    Guid? StepId,
    RevisionChangeKind Kind,
    string Description,
    string Reason,
    double Confidence,
    ImmutableArray<Guid> EvidenceEventIds);

public sealed record WorkflowRevision(
    Guid Id,
    Guid WorkflowId,
    int Version,
    Guid? ParentRevisionId,
    DateTimeOffset CreatedAtUtc,
    string Author,
    WorkflowRevisionKind Kind,
    string Summary,
    ImmutableArray<WorkflowStep> Steps,
    ImmutableArray<RevisionChange> Changes,
    double? Confidence = null,
    string? ModelId = null,
    string? PromptVersion = null);

public sealed record WorkflowDocument(
    Guid Id,
    string Name,
    string Goal,
    DateTimeOffset CreatedAtUtc,
    ImmutableArray<WorkflowParameter> Parameters,
    ImmutableArray<WorkflowRevision> Revisions)
{
    public WorkflowRevision CurrentRevision =>
        Revisions.OrderBy(revision => revision.Version).Last();

    public bool HasRevision(Guid revisionId) =>
        Revisions.Any(revision => revision.Id == revisionId);
}
