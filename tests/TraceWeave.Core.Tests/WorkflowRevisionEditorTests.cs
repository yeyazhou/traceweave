using System.Collections.Immutable;
using TraceWeave.Core.Capture;
using TraceWeave.Core.Editing;
using TraceWeave.Core.Generation;
using TraceWeave.Core.Optimization;
using TraceWeave.Core.Review;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Core.Tests;

public sealed class WorkflowRevisionEditorTests
{
    [Fact]
    public async Task ManualEditsCreateNewRevisions_AndRestoreDoesNotRewriteHistory()
    {
        var workflow = await CreateWorkflowAsync();
        var editor = new WorkflowRevisionEditor();
        var original = workflow.CurrentRevision;
        var firstStep = original.Steps[0];

        var edited = editor.UpdateStep(
            workflow,
            firstStep.Id,
            firstStep with { Title = "Open the correct settings page" },
            "Clarified the purpose of the step.");

        Assert.Equal(2, edited.Revisions.Length);
        Assert.Equal("Open the correct settings page", edited.CurrentRevision.Steps[0].Title);
        Assert.Equal(firstStep.Title, workflow.CurrentRevision.Steps[0].Title);
        Assert.Equal(original.Id, edited.CurrentRevision.ParentRevisionId);

        var restored = editor.RestoreRevision(edited, original.Id, "Undo the manual edit.");
        Assert.Equal(3, restored.CurrentRevision.Version);
        Assert.Equal(original.Steps[0].Title, restored.CurrentRevision.Steps[0].Title);
        Assert.Equal("Open the correct settings page", restored.Revisions[1].Steps[0].Title);
        Assert.Equal(WorkflowRevisionKind.Restore, restored.CurrentRevision.Kind);
    }

    [Fact]
    public async Task AiProposalsAreAppliedOnlyToTheReviewedCurrentRevision()
    {
        var workflow = await CreateWorkflowAsync();
        var current = workflow.CurrentRevision;
        var target = current.Steps[0];
        var proposal = new WorkflowChangeProposal(
            Guid.NewGuid(),
            ProposedChangeKind.RemoveStep,
            target.Id,
            null,
            null,
            null,
            "The step is contradicted by the later successful state transition.",
            0.97,
            target.Evidence.Select(item => item.CaptureEventId).ToImmutableArray());

        var review = new WorkflowReviewResult(
            workflow.Id,
            current.Id,
            "TestProvider",
            "advanced-model",
            DateTimeOffset.UtcNow,
            "One unnecessary step was identified.",
            ImmutableArray<WorkflowReviewFinding>.Empty,
            [proposal]);

        var editor = new WorkflowRevisionEditor();
        var corrected = editor.ApplyAiProposals(workflow, review, [proposal.Id]);

        Assert.Equal(WorkflowRevisionKind.AiReview, corrected.CurrentRevision.Kind);
        Assert.Equal("advanced-model", corrected.CurrentRevision.ModelId);
        Assert.DoesNotContain(corrected.CurrentRevision.Steps, step => step.Id == target.Id);
        Assert.Contains(workflow.CurrentRevision.Steps, step => step.Id == target.Id);
    }

    [Fact]
    public async Task StaleAiReviewCannotModifyANewerRevision()
    {
        var workflow = await CreateWorkflowAsync();
        var reviewedRevision = workflow.CurrentRevision;
        var editor = new WorkflowRevisionEditor();
        var changed = editor.UpdateStep(
            workflow,
            reviewedRevision.Steps[0].Id,
            reviewedRevision.Steps[0] with { Title = "Edited after review" },
            "User changed the step.");

        var review = new WorkflowReviewResult(
            workflow.Id,
            reviewedRevision.Id,
            "TestProvider",
            "advanced-model",
            DateTimeOffset.UtcNow,
            "Stale review",
            ImmutableArray<WorkflowReviewFinding>.Empty,
            ImmutableArray<WorkflowChangeProposal>.Empty);

        Assert.Throws<InvalidOperationException>(() =>
            editor.ApplyAiProposals(changed, review, []));
    }

    private static async Task<WorkflowDocument> CreateWorkflowAsync()
    {
        var recordingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var events = ImmutableArray.Create(
            new CaptureEvent(
                Guid.NewGuid(), recordingId, 1, now, CaptureEventKind.MouseClick, CaptureSource.MouseHook,
                "settings.exe", "Settings", "General", "general-tab", "TabItem"),
            new CaptureEvent(
                Guid.NewGuid(), recordingId, 2, now.AddSeconds(1), CaptureEventKind.MouseClick, CaptureSource.MouseHook,
                "settings.exe", "Settings", "Save", "save-button", "Button"));

        var generator = new FirstPassWorkflowGenerator(
            new RuleBasedWorkflowSynthesisProvider(),
            new FirstPassOptimizer());

        return (await generator.GenerateFirstPassAsync(
            new WorkflowSynthesisRequest(recordingId, "Update settings", events))).Workflow;
    }
}
