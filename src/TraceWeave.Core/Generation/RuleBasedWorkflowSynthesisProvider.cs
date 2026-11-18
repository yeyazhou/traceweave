using System.Collections.Immutable;
using TraceWeave.Core.Capture;
using TraceWeave.Core.Optimization;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Core.Generation;

/// <summary>
/// Conservative offline synthesis provider. It creates evidence-linked candidate steps;
/// the first-pass optimizer then removes or merges only well-supported noise.
/// A model-backed provider can replace this implementation without changing the pipeline.
/// </summary>
public sealed class RuleBasedWorkflowSynthesisProvider : IWorkflowSynthesisProvider
{
    private static readonly TimeSpan ClipboardCorrelationWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TextDuplicateWindow = TimeSpan.FromMilliseconds(900);

    public Task<WorkflowSynthesisDraft> SynthesizeAsync(
        WorkflowSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var events = request.Events
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.OccurredAtUtc)
            .ToArray();

        var steps = new List<WorkflowStep>();
        var parameters = new List<WorkflowParameter>();
        var findings = new List<OptimizationFinding>();
        var consumedClipboardCopies = new HashSet<Guid>();
        var clipboardCopies = events.Where(item => item.Kind == CaptureEventKind.ClipboardCopied).ToArray();
        var textEvents = new List<CaptureEvent>();
        var parameterIndex = 0;

        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (item.Kind)
            {
                case CaptureEventKind.MouseClick:
                    steps.Add(CreateStep(item, WorkflowActionKind.Click, "Click", "Activate the observed control.", 0.96));
                    break;

                case CaptureEventKind.MouseDoubleClick:
                    steps.Add(CreateStep(item, WorkflowActionKind.DoubleClick, "Double-click", "Double-click the observed control.", 0.96));
                    break;

                case CaptureEventKind.MouseDrag:
                    steps.Add(CreateStep(item, WorkflowActionKind.Drag, "Drag", "Drag the observed item or control.", 0.90));
                    break;

                case CaptureEventKind.MouseScroll:
                    steps.Add(CreateStep(
                        item,
                        WorkflowActionKind.Scroll,
                        "Scroll",
                        "Scroll the observed view. Direction and distance may need review.",
                        0.72,
                        StepReviewStatus.NeedsReview));
                    findings.Add(Finding(
                        "ScrollNeedsContext",
                        OptimizationSeverity.Warning,
                        "A scroll action was observed, but its purpose may depend on the surrounding screen state.",
                        [item.Id],
                        0.72));
                    break;

                case CaptureEventKind.KeyChord:
                {
                    var shortcut = NormalizeShortcut(item.GetMetadata("shortcut") ?? item.TextValue);
                    if (IsClipboardShortcut(shortcut, events, item))
                    {
                        break;
                    }

                    steps.Add(CreateStep(
                        item,
                        WorkflowActionKind.PressShortcut,
                        $"Press {shortcut}",
                        "Execute the observed keyboard shortcut.",
                        0.94,
                        shortcut: shortcut));
                    break;
                }

                case CaptureEventKind.TextCommitted:
                case CaptureEventKind.UiElementValueChanged:
                {
                    if (IsNearClipboardPaste(item, events))
                    {
                        break;
                    }

                    var duplicate = textEvents.LastOrDefault(previous =>
                        previous.TargetKey == item.TargetKey &&
                        string.Equals(previous.TextValue, item.TextValue, StringComparison.Ordinal) &&
                        Math.Abs((item.OccurredAtUtc - previous.OccurredAtUtc).TotalMilliseconds) <= TextDuplicateWindow.TotalMilliseconds);

                    if (duplicate is not null)
                    {
                        findings.Add(Finding(
                            "DuplicateTextObservation",
                            OptimizationSeverity.Information,
                            "Two capture sources observed the same committed text in the same control; the duplicate observation was collapsed.",
                            [duplicate.Id, item.Id],
                            0.97));
                        break;
                    }

                    textEvents.Add(item);
                    parameterIndex++;
                    var parameterName = $"input_{parameterIndex:000}";
                    parameters.Add(new WorkflowParameter(
                        parameterName,
                        $"Text entered into {DescribeTarget(item)}",
                        true,
                        item.Sensitivity));

                    var disposition = item.TextDisposition;
                    var status = disposition is TextCaptureDisposition.Redacted or TextCaptureDisposition.Suppressed or TextCaptureDisposition.Unavailable
                        ? StepReviewStatus.NeedsReview
                        : StepReviewStatus.Unreviewed;
                    var confidence = status == StepReviewStatus.NeedsReview ? 0.58 : 0.94;

                    steps.Add(CreateStep(
                        item,
                        WorkflowActionKind.EnterText,
                        $"Enter text in {DescribeTarget(item)}",
                        "Enter the committed text into the observed control.",
                        confidence,
                        status,
                        inputTemplate: $"{{{{{parameterName}}}}}",
                        rationale: status == StepReviewStatus.NeedsReview
                            ? "Text content was intentionally unavailable or redacted; supply the value when editing or running this workflow."
                            : "The text value is represented as a parameter rather than embedded in the workflow."));
                    break;
                }

                case CaptureEventKind.ClipboardPasted:
                {
                    var copy = FindMatchingCopy(item, clipboardCopies);
                    var evidence = ImmutableArray.CreateBuilder<EvidenceReference>();

                    if (copy is not null)
                    {
                        consumedClipboardCopies.Add(copy.Id);
                        evidence.Add(ToEvidence(copy, "clipboard-source"));
                        steps.Add(CreateStep(
                            copy,
                            WorkflowActionKind.CopyText,
                            $"Copy text from {DescribeTarget(copy)}",
                            "Copy the source value for a later paste.",
                            0.95,
                            origin: StepOrigin.Correlated));
                        findings.Add(Finding(
                            "ClipboardTransferCorrelated",
                            OptimizationSeverity.Information,
                            "A clipboard copy was linked to the later paste action.",
                            [copy.Id, item.Id],
                            0.96));
                    }
                    else
                    {
                        findings.Add(Finding(
                            "ClipboardSourceMissing",
                            OptimizationSeverity.NeedsHumanReview,
                            "A paste was observed without a confidently linked copy event. The source value is unknown.",
                            [item.Id],
                            0.55));
                    }

                    evidence.Add(ToEvidence(item, "clipboard-destination"));
                    steps.Add(new WorkflowStep(
                        Guid.NewGuid(),
                        steps.Count + 1,
                        $"Paste text into {DescribeTarget(item)}",
                        "Transfer the current clipboard value into the observed destination.",
                        WorkflowActionKind.PasteText,
                        ToTarget(item),
                        "{{clipboard}}",
                        null,
                        ImmutableArray<string>.Empty,
                        ImmutableArray<string>.Empty,
                        evidence.ToImmutable(),
                        true,
                        copy is null ? StepOrigin.Observed : StepOrigin.Correlated,
                        copy is null ? StepReviewStatus.NeedsReview : StepReviewStatus.Unreviewed,
                        copy is null ? 0.55 : 0.96,
                        copy is null ? "The source clipboard event could not be linked confidently." : "Copy and paste evidence were correlated."));
                    break;
                }

                case CaptureEventKind.ClipboardCopied:
                    // A copy is emitted as a workflow step only when it is not linked to a paste.
                    break;

                case CaptureEventKind.WindowActivated:
                    steps.Add(CreateStep(
                        item,
                        WorkflowActionKind.SwitchApplication,
                        $"Switch to {item.ProcessName ?? "application"}",
                        "Activate the observed application window.",
                        0.86));
                    break;

                case CaptureEventKind.DelayObserved:
                {
                    var condition = item.GetMetadata("expected_condition");
                    if (!string.IsNullOrWhiteSpace(condition))
                    {
                        steps.Add(CreateStep(
                            item,
                            WorkflowActionKind.WaitForCondition,
                            "Wait for the observed state",
                            "Wait for a verifiable state instead of sleeping for a fixed duration.",
                            0.82,
                            postconditions: [condition]));
                    }
                    else
                    {
                        findings.Add(Finding(
                            "UnanchoredDelayRemoved",
                            OptimizationSeverity.Information,
                            "A fixed delay had no observable state condition and was omitted from the reusable workflow.",
                            [item.Id],
                            0.91));
                    }
                    break;
                }

                case CaptureEventKind.FileCreated:
                    steps.Add(CreateStep(item, WorkflowActionKind.CreateFile, "Create file", "Create the observed file.", 0.90));
                    break;

                case CaptureEventKind.FileChanged:
                    steps.Add(CreateStep(item, WorkflowActionKind.ChangeFile, "Update file", "Update the observed file.", 0.88));
                    break;

                case CaptureEventKind.FileRenamed:
                    steps.Add(CreateStep(item, WorkflowActionKind.RenameFile, "Rename file", "Rename the observed file.", 0.90));
                    break;


                case CaptureEventKind.UiElementFocused:
                case CaptureEventKind.ScreenshotCaptured:
                case CaptureEventKind.OcrTextDetected:
                case CaptureEventKind.ProcessStarted:
                case CaptureEventKind.ProcessExited:
                case CaptureEventKind.RecordingMarker:
                    // Context/evidence events are retained in the event journal, not emitted as actions.
                    break;
            }
        }

        foreach (var copy in clipboardCopies.Where(item => !consumedClipboardCopies.Contains(item.Id)))
        {
            findings.Add(Finding(
                "UnpairedClipboardCopyRemoved",
                OptimizationSeverity.Information,
                "A clipboard copy had no matching paste in the recording, so the first pass removed it as likely incidental.",
                [copy.Id],
                0.86));
        }
        var name = string.IsNullOrWhiteSpace(request.UserIntent) ? "Recorded workflow" : request.UserIntent.Trim();
        var goal = string.IsNullOrWhiteSpace(request.UserIntent)
            ? "Reproduce the task demonstrated in the recording."
            : request.UserIntent.Trim();

        var draft = new WorkflowSynthesisDraft(
            name,
            goal,
            parameters.ToImmutableArray(),
            Renumber(steps).ToImmutableArray(),
            findings.ToImmutableArray());

        return Task.FromResult(draft);
    }

    private static WorkflowStep CreateStep(
        CaptureEvent item,
        WorkflowActionKind action,
        string title,
        string intent,
        double confidence,
        StepReviewStatus reviewStatus = StepReviewStatus.Unreviewed,
        string? inputTemplate = null,
        string? shortcut = null,
        ImmutableArray<string> postconditions = default,
        StepOrigin origin = StepOrigin.Observed,
        string? rationale = null)
    {
        var evidence = ImmutableArray.Create(ToEvidence(item, "action"));
        var preconditions = string.IsNullOrWhiteSpace(item.AutomationId)
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create("The target control is present and enabled.");

        return new WorkflowStep(
            Guid.NewGuid(),
            0,
            title,
            intent,
            action,
            ToTarget(item),
            inputTemplate,
            shortcut,
            preconditions,
            postconditions.IsDefault ? ImmutableArray<string>.Empty : postconditions,
            evidence,
            true,
            origin,
            reviewStatus,
            confidence,
            rationale ?? "Derived from a directly observed Windows event.");
    }

    private static WorkflowTarget ToTarget(CaptureEvent item) =>
        new(
            item.ProcessName,
            item.WindowTitle,
            item.AutomationId,
            item.ElementName,
            item.ControlType,
            item.WindowTitle);

    private static EvidenceReference ToEvidence(CaptureEvent item, string role) =>
        new(item.Id, role, item.OccurredAtUtc, $"{item.Kind} in {DescribeTarget(item)}");

    private static string DescribeTarget(CaptureEvent item) =>
        !string.IsNullOrWhiteSpace(item.ElementName) ? item.ElementName :
        !string.IsNullOrWhiteSpace(item.AutomationId) ? item.AutomationId :
        !string.IsNullOrWhiteSpace(item.ProcessName) ? item.ProcessName :
        "target";

    private static string NormalizeShortcut(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return "UNKNOWN";
        }

        return string.Join("+", shortcut
            .Split(['+', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.ToUpperInvariant()));
    }

    private static bool IsClipboardShortcut(string shortcut, IReadOnlyList<CaptureEvent> events, CaptureEvent keyEvent)
    {
        var expectedKind = shortcut switch
        {
            "CTRL+C" or "CONTROL+C" => CaptureEventKind.ClipboardCopied,
            "CTRL+V" or "CONTROL+V" => CaptureEventKind.ClipboardPasted,
            _ => (CaptureEventKind?)null
        };

        return expectedKind is not null && events.Any(candidate =>
            candidate.Kind == expectedKind &&
            Math.Abs((candidate.OccurredAtUtc - keyEvent.OccurredAtUtc).TotalMilliseconds) <= 2_000);
    }

    private static bool IsNearClipboardPaste(CaptureEvent textEvent, IReadOnlyList<CaptureEvent> events) =>
        events.Any(candidate =>
            candidate.Kind == CaptureEventKind.ClipboardPasted &&
            candidate.TargetKey == textEvent.TargetKey &&
            Math.Abs((candidate.OccurredAtUtc - textEvent.OccurredAtUtc).TotalMilliseconds) <= 1_500);

    private static CaptureEvent? FindMatchingCopy(CaptureEvent paste, IReadOnlyList<CaptureEvent> copies) =>
        copies
            .Where(copy => copy.OccurredAtUtc <= paste.OccurredAtUtc &&
                           paste.OccurredAtUtc - copy.OccurredAtUtc <= ClipboardCorrelationWindow)
            .Where(copy => string.IsNullOrWhiteSpace(paste.ClipboardCorrelationId) ||
                           string.Equals(copy.ClipboardCorrelationId, paste.ClipboardCorrelationId, StringComparison.Ordinal))
            .OrderByDescending(copy => copy.OccurredAtUtc)
            .FirstOrDefault();

    private static OptimizationFinding Finding(
        string code,
        OptimizationSeverity severity,
        string message,
        ImmutableArray<Guid> evidence,
        double confidence) =>
        new(code, severity, message, evidence, confidence);

    private static IEnumerable<WorkflowStep> Renumber(IEnumerable<WorkflowStep> steps)
    {
        var order = 0;
        foreach (var step in steps)
        {
            yield return step with { Order = ++order };
        }
    }
}
