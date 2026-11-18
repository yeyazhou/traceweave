using System.Collections.Immutable;
using System.IO;
using TraceWeave.Core.Capture;
using TraceWeave.Core.Editing;
using TraceWeave.Core.Review;
using TraceWeave.Core.Workflows;
using TraceWeave.Infrastructure.Export;

namespace TraceWeave.Core.Tests;

public sealed class WorkflowReviewAndExportTests
{
    [Fact]
    public void TextRedactorBlocksSecretLikeValuesBeforePersistence()
    {
        var redactor = new TraceWeave.Infrastructure.Windows.PatternTextRedactor();

        var secret = redactor.Inspect("Authorization: Bearer abcdefghijklmnopqrstuvwxyz123456");
        var huge = redactor.Inspect(new string('x', 20_000));

        Assert.Equal(TextCaptureDisposition.Redacted, secret.Disposition);
        Assert.Null(secret.Value);
        Assert.Equal(TextCaptureDisposition.Suppressed, huge.Disposition);
        Assert.Null(huge.Value);
    }

    [Fact]
    public async Task OfflineReviewFindsWeakSelectorAndAddsFinalVerificationProposal()
    {        var workflow = CreateWorkflow(
            new WorkflowStep(
                Guid.NewGuid(),
                1,
                "Click save",
                "Save the current work.",
                WorkflowActionKind.Click,
                new WorkflowTarget(Application: "notepad.exe", FallbackDescription: "Save"),
                null,
                null,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray.Create(new EvidenceReference(Guid.NewGuid(), "click", DateTimeOffset.UtcNow)),
                true,
                StepOrigin.FirstPassSynthesized,
                StepReviewStatus.Unreviewed,
                0.88));

        var reviewer = new RuleBasedWorkflowReviewProvider();
        var result = await reviewer.ReviewAsync(new WorkflowReviewRequest(
            workflow,
            workflow.CurrentRevision.Id,
            "Check for unnecessary operations.",
            false,
            false));

        Assert.Contains(result.Findings, finding => finding.Kind == WorkflowFindingKind.FragileSelector);
        Assert.Contains(result.Findings, finding => finding.Kind == WorkflowFindingKind.WeakVerification);
        Assert.Contains(result.Proposals, proposal => proposal.Kind == ProposedChangeKind.InsertStep);
    }

    [Fact]
    public async Task ApplyingReviewProposalsCreatesNewAiRevision()
    {
        var stepId = Guid.NewGuid();
        var workflow = CreateWorkflow(
            new WorkflowStep(
                stepId,
                1,
                "Wait",
                "Wait for the result.",
                WorkflowActionKind.WaitForCondition,
                new WorkflowTarget(Application: "demo.exe", ElementName: "Result"),
                null,
                null,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray.Create(new EvidenceReference(Guid.NewGuid(), "wait", DateTimeOffset.UtcNow)),
                true,
                StepOrigin.FirstPassSynthesized,
                StepReviewStatus.NeedsReview,
                0.8));

        var reviewer = new RuleBasedWorkflowReviewProvider();
        var review = await reviewer.ReviewAsync(new WorkflowReviewRequest(
            workflow,
            workflow.CurrentRevision.Id,
            null,
            false,
            false));

        var editor = new WorkflowRevisionEditor();
        var revised = editor.ApplyAiProposals(
            workflow,
            review,
            review.Proposals.Select(proposal => proposal.Id));

        Assert.Equal(2, revised.Revisions.Length);
        Assert.Equal(WorkflowRevisionKind.AiReview, revised.CurrentRevision.Kind);
        Assert.True(revised.CurrentRevision.Steps.Length >= 1);
    }

    [Fact]
    public async Task ExportWritesWorkflowJsonAndSkillMarkdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TraceWeave.Tests", Guid.NewGuid().ToString("N"));
        var workflow = CreateWorkflow(
            new WorkflowStep(
                Guid.NewGuid(),
                1,
                "Open project",
                "Open the project.",
                WorkflowActionKind.Click,
                new WorkflowTarget(Application: "demo.exe", ElementName: "Open"),
                null,
                null,
                ["Project is available."],
                ["Project window is visible."],
                ImmutableArray<EvidenceReference>.Empty,
                true,
                StepOrigin.FirstPassSynthesized,
                StepReviewStatus.Accepted,
                0.95));

        var exporter = new WorkflowExporter();
        var result = await exporter.ExportAsync(workflow, directory);

        Assert.True(File.Exists(result.WorkflowJsonPath));
        Assert.True(File.Exists(result.SkillMarkdownPath));

        var markdown = await File.ReadAllTextAsync(result.SkillMarkdownPath);
        Assert.Contains("# Recorded workflow", markdown);
        Assert.Contains("Open project", markdown);
        Assert.Contains("Project window is visible.", markdown);
    }

    private static WorkflowDocument CreateWorkflow(params WorkflowStep[] steps)
    {
        var workflowId = Guid.NewGuid();
        var revision = new WorkflowRevision(
            Guid.NewGuid(),
            workflowId,
            1,
            null,
            DateTimeOffset.UtcNow,
            "TraceWeave",
            WorkflowRevisionKind.FirstPass,
            "Test revision",
            steps.ToImmutableArray(),
            ImmutableArray<RevisionChange>.Empty,
            0.9,
            "test");

        return new WorkflowDocument(
            workflowId,
            "Recorded workflow",
            "Reproduce the recorded task.",
            DateTimeOffset.UtcNow,
            ImmutableArray<WorkflowParameter>.Empty,
            ImmutableArray.Create(revision));
    }
}
