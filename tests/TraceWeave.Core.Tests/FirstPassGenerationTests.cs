using System.Collections.Immutable;
using TraceWeave.Core.Capture;
using TraceWeave.Core.Generation;
using TraceWeave.Core.Optimization;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Core.Tests;

public sealed class FirstPassGenerationTests
{
    [Fact]
    public async Task FirstPass_MergesDuplicateClicks_AndRemovesUnanchoredDelay()
    {
        var recordingId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        var target = new
        {
            ProcessName = "settings.exe",
            WindowTitle = "Settings",
            ElementName = "Save",
            AutomationId = "save-button",
            ControlType = "Button"
        };

        var events = ImmutableArray.Create(
            NewEvent(recordingId, 1, start, CaptureEventKind.MouseClick, target.ProcessName, target.WindowTitle, target.ElementName, target.AutomationId, target.ControlType),
            NewEvent(recordingId, 2, start.AddMilliseconds(240), CaptureEventKind.MouseClick, target.ProcessName, target.WindowTitle, target.ElementName, target.AutomationId, target.ControlType),
            NewEvent(recordingId, 3, start.AddSeconds(2), CaptureEventKind.DelayObserved));

        var generator = new FirstPassWorkflowGenerator(
            new RuleBasedWorkflowSynthesisProvider(),
            new FirstPassOptimizer());

        var result = await generator.GenerateFirstPassAsync(
            new WorkflowSynthesisRequest(recordingId, "Save settings", events));

        Assert.Single(result.Workflow.CurrentRevision.Steps);
        Assert.Equal(WorkflowActionKind.Click, result.Workflow.CurrentRevision.Steps[0].Action);
        Assert.Equal(2, result.Workflow.CurrentRevision.Steps[0].Evidence.Length);
        Assert.Equal(1, result.Report.DuplicateStepsMerged);
        Assert.Equal(1, result.Report.NoiseEventsRemoved);
        Assert.Equal(3, result.Report.InputEventCount);
        Assert.Equal(2, result.Report.CandidateStepCount);
        Assert.Equal(1, result.Report.OutputStepCount);
    }

    [Fact]
    public async Task FirstPass_CorrelatesClipboardCopyAndPaste_AndSuppressesShortcutDuplicates()
    {
        var recordingId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        var source = NewEvent(
            recordingId, 2, start.AddMilliseconds(100), CaptureEventKind.ClipboardCopied,
            "browser.exe", "Source page", "API address", "source-address", "TextBox",
            clipboardId: "clipboard-7");
        var paste = NewEvent(
            recordingId, 4, start.AddMilliseconds(900), CaptureEventKind.ClipboardPasted,
            "settings.exe", "API settings", "Base URL", "base-url", "TextBox",
            clipboardId: "clipboard-7");

        var events = ImmutableArray.Create(
            NewEvent(recordingId, 1, start, CaptureEventKind.KeyChord, shortcut: "CTRL+C"),
            source,
            NewEvent(recordingId, 3, start.AddMilliseconds(800), CaptureEventKind.KeyChord, shortcut: "CTRL+V"),
            paste);

        var generator = new FirstPassWorkflowGenerator(
            new RuleBasedWorkflowSynthesisProvider(),
            new FirstPassOptimizer());

        var result = await generator.GenerateFirstPassAsync(
            new WorkflowSynthesisRequest(recordingId, "Copy the API address into settings", events));

        var steps = result.Workflow.CurrentRevision.Steps;
        Assert.Equal(2, steps.Length);
        Assert.Equal(WorkflowActionKind.CopyText, steps[0].Action);
        Assert.Equal(WorkflowActionKind.PasteText, steps[1].Action);
        Assert.Equal("{{clipboard}}", steps[1].InputTemplate);
        Assert.Contains(steps[1].Evidence, evidence => evidence.CaptureEventId == source.Id);
        Assert.Contains(steps[1].Evidence, evidence => evidence.CaptureEventId == paste.Id);
        Assert.Equal(1, result.Report.ClipboardTransfersCorrelated);
        Assert.DoesNotContain(steps, step => step.Action == WorkflowActionKind.PressShortcut);
    }

    [Fact]
    public async Task FirstPass_RedactedTextBecomesRequiredSecretParameter_NotPlaintext()
    {
        var recordingId = Guid.NewGuid();
        var secret = "do-not-store-this-value";
        var input = NewEvent(
            recordingId, 1, DateTimeOffset.UtcNow, CaptureEventKind.TextCommitted,
            "client.exe", "API key settings", "API Key", "api-key", "Edit",
            text: null,
            disposition: TextCaptureDisposition.Redacted,
            sensitivity: SensitivityLevel.Secret);

        var generator = new FirstPassWorkflowGenerator(
            new RuleBasedWorkflowSynthesisProvider(),
            new FirstPassOptimizer());

        var result = await generator.GenerateFirstPassAsync(
            new WorkflowSynthesisRequest(recordingId, "Configure API key", [input]));

        var step = Assert.Single(result.Workflow.CurrentRevision.Steps);
        var parameter = Assert.Single(result.Workflow.Parameters);

        Assert.Equal(WorkflowActionKind.EnterText, step.Action);
        Assert.Equal(StepReviewStatus.NeedsReview, step.ReviewStatus);
        Assert.Equal("{{input_001}}", step.InputTemplate);
        Assert.Equal(SensitivityLevel.Secret, parameter.Sensitivity);
        Assert.Null(parameter.ExampleValue);
        Assert.DoesNotContain(secret, step.ToString(), StringComparison.Ordinal);
    }

    private static CaptureEvent NewEvent(
        Guid recordingId,
        long sequence,
        DateTimeOffset time,
        CaptureEventKind kind,
        string? process = null,
        string? window = null,
        string? element = null,
        string? automationId = null,
        string? controlType = null,
        string? text = null,
        TextCaptureDisposition disposition = TextCaptureDisposition.NotApplicable,
        SensitivityLevel sensitivity = SensitivityLevel.Public,
        string? clipboardId = null,
        string? shortcut = null)
    {
        var metadata = shortcut is null
            ? null
            : ImmutableDictionary<string, string>.Empty.Add("shortcut", shortcut);

        return new CaptureEvent(
            Guid.NewGuid(),
            recordingId,
            sequence,
            time,
            kind,
            kind switch
            {
                CaptureEventKind.ClipboardCopied or CaptureEventKind.ClipboardPasted => CaptureSource.ClipboardListener,
                CaptureEventKind.TextCommitted or CaptureEventKind.UiElementValueChanged => CaptureSource.UiAutomation,
                CaptureEventKind.KeyChord => CaptureSource.KeyboardHook,
                _ => CaptureSource.MouseHook
            },
            process,
            window,
            element,
            automationId,
            controlType,
            text,
            disposition,
            clipboardId,
            Metadata: metadata,
            Sensitivity: sensitivity);
    }
}
