using System.Collections.Immutable;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Core.Review;

/// <summary>
/// Conservative second-pass reviewer used offline when no remote model is configured.
/// It produces findings and explicit proposals; it never mutates the workflow.
/// </summary>
public sealed class RuleBasedWorkflowReviewProvider : IWorkflowReviewProvider
{
    public Task<WorkflowReviewResult> ReviewAsync(
        WorkflowReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.RevisionId != request.Workflow.CurrentRevision.Id)
        {
            throw new InvalidOperationException("The requested review revision is stale.");
        }

        var steps = request.Workflow.CurrentRevision.Steps;
        var findings = ImmutableArray.CreateBuilder<WorkflowReviewFinding>();
        var proposals = ImmutableArray.CreateBuilder<WorkflowChangeProposal>();

        if (steps.IsDefaultOrEmpty)
        {
            findings.Add(new WorkflowReviewFinding(
                Guid.NewGuid(),
                WorkflowFindingKind.MissingStep,
                FindingSeverity.Critical,
                "Workflow has no executable steps",
                "The current revision cannot reproduce the task because it contains no actions.",
                0.99,
                ImmutableArray<Guid>.Empty));
        }

        for (var index = 0; index < steps.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = steps[index];

            if (step.Action is WorkflowActionKind.Click or WorkflowActionKind.DoubleClick or WorkflowActionKind.EnterText or WorkflowActionKind.PasteText)
            {
                var target = step.Target;
                if (target is null ||
                    (string.IsNullOrWhiteSpace(target.AutomationId) &&
                     string.IsNullOrWhiteSpace(target.ElementName) &&
                     string.IsNullOrWhiteSpace(target.ControlType)))
                {
                    findings.Add(new WorkflowReviewFinding(
                        Guid.NewGuid(),
                        WorkflowFindingKind.FragileSelector,
                        FindingSeverity.Warning,
                        $"Step {step.Order} has a weak semantic selector",
                        "The step relies mainly on a fallback description or window context. Prefer a stable UI Automation id, accessible name, or control type.",
                        0.91,
                        EvidenceIds(step),
                        step.Id));
                }
            }

            if (step.Action == WorkflowActionKind.Scroll && step.Postconditions.IsDefaultOrEmpty)
            {
                findings.Add(new WorkflowReviewFinding(
                    Guid.NewGuid(),
                    WorkflowFindingKind.MissingPostcondition,
                    FindingSeverity.Warning,
                    $"Step {step.Order} scrolls without a state check",
                    "The scroll may be incidental or may be required to reveal a target. A reusable workflow should verify the resulting state.",
                    0.82,
                    EvidenceIds(step),
                    step.Id));
            }

            if (step.Action == WorkflowActionKind.WaitForCondition && step.Postconditions.IsDefaultOrEmpty)
            {
                findings.Add(new WorkflowReviewFinding(
                    Guid.NewGuid(),
                    WorkflowFindingKind.InvalidValue,
                    FindingSeverity.Warning,
                    $"Step {step.Order} waits without a condition",
                    "A wait without an observable condition is fragile and should be replaced with a verifiable state.",
                    0.94,
                    EvidenceIds(step),
                    step.Id));
                proposals.Add(new WorkflowChangeProposal(
                    Guid.NewGuid(),
                    ProposedChangeKind.ReplaceStep,
                    step.Id,
                    step with
                    {
                        Title = "Wait for a verifiable state",
                        Intent = "Wait until the target application exposes the expected state.",
                        Postconditions = ["The expected state is observable before continuing."],
                        ReviewStatus = StepReviewStatus.NeedsReview,
                        Rationale = "The original wait had no explicit condition; the reviewer replaced it with a reviewable semantic condition."
                    },
                    null,
                    null,
                    "Replace an unanchored wait with an explicit, reviewable condition.",
                    0.94,
                    EvidenceIds(step)));
            }
        }

        for (var index = 1; index < steps.Length; index++)
        {
            var previous = steps[index - 1];
            var current = steps[index];

            if (SameAction(previous, current) &&
                previous.Action is WorkflowActionKind.Click or WorkflowActionKind.DoubleClick or WorkflowActionKind.SwitchApplication)
            {
                findings.Add(new WorkflowReviewFinding(
                    Guid.NewGuid(),
                    WorkflowFindingKind.RedundantStep,
                    FindingSeverity.Warning,
                    $"Step {current.Order} may duplicate step {previous.Order}",
                    "Two adjacent steps perform the same action against the same semantic target or application. The second action is a candidate for removal.",
                    0.90,
                    EvidenceIds(previous).AddRange(EvidenceIds(current)).Distinct().ToImmutableArray(),
                    current.Id));

                proposals.Add(new WorkflowChangeProposal(
                    Guid.NewGuid(),
                    ProposedChangeKind.RemoveStep,
                    current.Id,
                    null,
                    null,
                    null,
                    "Remove an adjacent duplicate action while preserving the first observed action.",
                    0.90,
                    EvidenceIds(previous).AddRange(EvidenceIds(current)).Distinct().ToImmutableArray()));
            }

            if (previous.Action == WorkflowActionKind.CopyText &&
                current.Action != WorkflowActionKind.PasteText &&
                index + 1 == steps.Length)
            {
                findings.Add(new WorkflowReviewFinding(
                    Guid.NewGuid(),
                    WorkflowFindingKind.SimplificationOpportunity,
                    FindingSeverity.Information,
                    $"Step {previous.Order} copies data that is never used",
                    "The recording ended after the copy, so the copy may be incidental rather than part of the reusable task.",
                    0.74,
                    EvidenceIds(previous),
                    previous.Id));

                proposals.Add(new WorkflowChangeProposal(
                    Guid.NewGuid(),
                    ProposedChangeKind.RemoveStep,
                    previous.Id,
                    null,
                    null,
                    null,
                    "Remove an unconsumed clipboard copy from the end of the workflow.",
                    0.74,
                    EvidenceIds(previous)));
            }
        }

        if (steps.Length > 0 &&
            steps.All(step => step.Action is not WorkflowActionKind.VerifyState) &&
            !steps[^1].Postconditions.Any())
        {
            var last = steps[^1];
            var verificationStep = new WorkflowStep(
                Guid.NewGuid(),
                last.Order + 1,
                "Verify final state",
                "Confirm that the intended task result is visible before reporting success.",
                WorkflowActionKind.VerifyState,
                last.Target,
                null,
                null,
                ["The previous action completed successfully."],
                ["The intended result is visible and stable."],
                last.Evidence,
                true,
                StepOrigin.AiProposed,
                StepReviewStatus.NeedsReview,
                0.71,
                "The workflow has no explicit final verification; this is a conservative inferred check.");

            findings.Add(new WorkflowReviewFinding(
                Guid.NewGuid(),
                WorkflowFindingKind.WeakVerification,
                FindingSeverity.Warning,
                "Workflow has no final verification",
                "The workflow can finish without checking whether the intended result actually occurred.",
                0.89,
                EvidenceIds(last),
                last.Id));

            proposals.Add(new WorkflowChangeProposal(
                Guid.NewGuid(),
                ProposedChangeKind.InsertStep,
                null,
                null,
                verificationStep,
                steps.Length + 1,
                "Add a final verification step so execution can stop safely when the expected state is not reached.",
                0.71,
                EvidenceIds(last)));
        }

        var summary = findings.Count == 0
            ? "No high-confidence structural issues were found by the offline second-pass reviewer."
            : $"Offline second-pass review found {findings.Count} item(s) and proposed {proposals.Count} selective change(s).";

        return Task.FromResult(new WorkflowReviewResult(
            request.Workflow.Id,
            request.RevisionId,
            "TraceWeave",
            "rule-based-review-v1",
            DateTimeOffset.UtcNow,
            summary,
            findings.ToImmutable(),
            proposals.ToImmutable()));
    }

    private static bool SameAction(WorkflowStep left, WorkflowStep right) =>
        left.Action == right.Action &&
        left.Target == right.Target &&
        string.Equals(left.Shortcut, right.Shortcut, StringComparison.OrdinalIgnoreCase);

    private static ImmutableArray<Guid> EvidenceIds(WorkflowStep step) =>
        step.Evidence.Select(item => item.CaptureEventId).ToImmutableArray();
}
