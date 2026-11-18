using System.Collections.Immutable;
using TraceWeave.Core.Review;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Core.Editing;

/// <summary>
/// All edits create a new immutable revision. Historical snapshots are never modified.
/// </summary>
public sealed class WorkflowRevisionEditor
{
    public WorkflowDocument UpdateStep(
        WorkflowDocument workflow,
        Guid stepId,
        WorkflowStep updatedStep,
        string reason,
        string author = "Human")
    {
        var current = workflow.CurrentRevision;
        var index = IndexOf(current.Steps, stepId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Step {stepId} was not found in revision {current.Version}.");
        }

        var steps = current.Steps.SetItem(index, updatedStep with { Id = stepId });
        return AppendRevision(
            workflow,
            current,
            Renumber(steps),
            WorkflowRevisionKind.HumanEdit,
            author,
            reason,
            [new RevisionChange(stepId, RevisionChangeKind.Modified, "Updated workflow step.", reason, 1.0, ImmutableArray<Guid>.Empty)]);
    }

    public WorkflowDocument RemoveStep(
        WorkflowDocument workflow,
        Guid stepId,
        string reason,
        string author = "Human")
    {
        var current = workflow.CurrentRevision;
        if (IndexOf(current.Steps, stepId) < 0)
        {
            throw new KeyNotFoundException($"Step {stepId} was not found in revision {current.Version}.");
        }

        var steps = current.Steps.Where(step => step.Id != stepId).ToImmutableArray();
        return AppendRevision(
            workflow,
            current,
            Renumber(steps),
            WorkflowRevisionKind.HumanEdit,
            author,
            reason,
            [new RevisionChange(stepId, RevisionChangeKind.Removed, "Removed workflow step.", reason, 1.0, ImmutableArray<Guid>.Empty)]);
    }

    public WorkflowDocument AddStep(
        WorkflowDocument workflow,
        WorkflowStep step,
        int oneBasedOrder,
        string reason,
        string author = "Human")
    {
        var current = workflow.CurrentRevision;
        var list = current.Steps.ToList();
        var index = Math.Clamp(oneBasedOrder - 1, 0, list.Count);
        list.Insert(index, step);
        return AppendRevision(
            workflow,
            current,
            Renumber(list.ToImmutableArray()),
            WorkflowRevisionKind.HumanEdit,
            author,
            reason,
            [new RevisionChange(step.Id, RevisionChangeKind.Added, "Added workflow step.", reason, 1.0, step.Evidence.Select(item => item.CaptureEventId).ToImmutableArray())]);
    }

    public WorkflowDocument MoveStep(
        WorkflowDocument workflow,
        Guid stepId,
        int newOneBasedOrder,
        string reason,
        string author = "Human")
    {
        var current = workflow.CurrentRevision;
        var list = current.Steps.ToList();
        var oldIndex = list.FindIndex(step => step.Id == stepId);
        if (oldIndex < 0)
        {
            throw new KeyNotFoundException($"Step {stepId} was not found in revision {current.Version}.");
        }

        var step = list[oldIndex];
        list.RemoveAt(oldIndex);
        var newIndex = Math.Clamp(newOneBasedOrder - 1, 0, list.Count);
        list.Insert(newIndex, step);

        return AppendRevision(
            workflow,
            current,
            Renumber(list.ToImmutableArray()),
            WorkflowRevisionKind.HumanEdit,
            author,
            reason,
            [new RevisionChange(stepId, RevisionChangeKind.Reordered, $"Moved step to position {newIndex + 1}.", reason, 1.0, step.Evidence.Select(item => item.CaptureEventId).ToImmutableArray())]);
    }

    public WorkflowDocument ApplyAiProposals(
        WorkflowDocument workflow,
        WorkflowReviewResult review,
        IEnumerable<Guid> acceptedProposalIds)
    {
        if (review.WorkflowId != workflow.Id || review.ReviewedRevisionId != workflow.CurrentRevision.Id)
        {
            throw new InvalidOperationException("The review is stale or belongs to a different workflow. Review the current revision before applying proposals.");
        }

        var accepted = acceptedProposalIds.ToHashSet();
        var proposals = review.Proposals.Where(proposal => accepted.Contains(proposal.Id)).ToArray();
        if (proposals.Length == 0)
        {
            return workflow;
        }

        var current = workflow.CurrentRevision;
        var list = current.Steps.ToList();
        var changes = ImmutableArray.CreateBuilder<RevisionChange>();

        foreach (var proposal in proposals)
        {
            switch (proposal.Kind)
            {
                case ProposedChangeKind.RemoveStep:
                {
                    var index = list.FindIndex(step => step.Id == proposal.TargetStepId);
                    if (index < 0) throw new InvalidOperationException("AI proposal targets a step that no longer exists.");
                    list.RemoveAt(index);
                    break;
                }
                case ProposedChangeKind.ReplaceStep:
                {
                    var index = list.FindIndex(step => step.Id == proposal.TargetStepId);
                    if (index < 0 || proposal.ReplacementStep is null) throw new InvalidOperationException("AI replacement proposal is incomplete or stale.");
                    list[index] = proposal.ReplacementStep with { Id = list[index].Id };
                    break;
                }
                case ProposedChangeKind.InsertStep:
                {
                    if (proposal.InsertedStep is null) throw new InvalidOperationException("AI insertion proposal has no step.");
                    var index = Math.Clamp((proposal.NewOrder ?? (list.Count + 1)) - 1, 0, list.Count);
                    list.Insert(index, proposal.InsertedStep);
                    break;
                }
                case ProposedChangeKind.MoveStep:
                {
                    var index = list.FindIndex(step => step.Id == proposal.TargetStepId);
                    if (index < 0) throw new InvalidOperationException("AI move proposal targets a step that no longer exists.");
                    var step = list[index];
                    list.RemoveAt(index);
                    var destination = Math.Clamp((proposal.NewOrder ?? (list.Count + 1)) - 1, 0, list.Count);
                    list.Insert(destination, step);
                    break;
                }
            }

            changes.Add(new RevisionChange(
                proposal.TargetStepId ?? proposal.InsertedStep?.Id,
                proposal.Kind switch
                {
                    ProposedChangeKind.RemoveStep => RevisionChangeKind.Removed,
                    ProposedChangeKind.ReplaceStep => RevisionChangeKind.Modified,
                    ProposedChangeKind.InsertStep => RevisionChangeKind.Added,
                    ProposedChangeKind.MoveStep => RevisionChangeKind.Reordered,
                    _ => RevisionChangeKind.Modified
                },
                proposal.Kind.ToString(),
                proposal.Reason,
                proposal.Confidence,
                proposal.EvidenceEventIds));
        }

        return AppendRevision(
            workflow,
            current,
            Renumber(list.ToImmutableArray()),
            WorkflowRevisionKind.AiReview,
            $"{review.Provider}/{review.ModelId}",
            review.Summary,
            changes.ToImmutable(),
            review.ModelId);
    }

    public WorkflowDocument RestoreRevision(
        WorkflowDocument workflow,
        Guid revisionId,
        string reason,
        string author = "Human")
    {
        var target = workflow.Revisions.SingleOrDefault(revision => revision.Id == revisionId)
            ?? throw new KeyNotFoundException($"Revision {revisionId} was not found.");

        var current = workflow.CurrentRevision;
        return AppendRevision(
            workflow,
            current,
            Renumber(target.Steps),
            WorkflowRevisionKind.Restore,
            author,
            reason,
            [new RevisionChange(null, RevisionChangeKind.Restored, $"Restored content from version {target.Version}.", reason, 1.0, ImmutableArray<Guid>.Empty)]);
    }

    private static WorkflowDocument AppendRevision(
        WorkflowDocument workflow,
        WorkflowRevision parent,
        ImmutableArray<WorkflowStep> steps,
        WorkflowRevisionKind kind,
        string author,
        string summary,
        ImmutableArray<RevisionChange> changes,
        string? modelId = null)
    {
        var revision = new WorkflowRevision(
            Guid.NewGuid(),
            workflow.Id,
            parent.Version + 1,
            parent.Id,
            DateTimeOffset.UtcNow,
            author,
            kind,
            summary,
            steps,
            changes,
            steps.IsDefaultOrEmpty ? 0 : steps.Average(step => step.Confidence),
            modelId);

        return workflow with { Revisions = workflow.Revisions.Add(revision) };
    }

    private static int IndexOf(ImmutableArray<WorkflowStep> steps, Guid id)
    {
        for (var index = 0; index < steps.Length; index++)
        {
            if (steps[index].Id == id) return index;
        }

        return -1;
    }

    private static ImmutableArray<WorkflowStep> Renumber(ImmutableArray<WorkflowStep> steps) =>
        steps.Select((step, index) => step with { Order = index + 1 }).ToImmutableArray();
}
