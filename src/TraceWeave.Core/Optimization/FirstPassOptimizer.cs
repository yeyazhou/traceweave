using System.Collections.Immutable;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Core.Optimization;

/// <summary>
/// Deterministic cleanup that runs after synthesis on every first pass.
/// It only removes/merges events when the evidence supports that decision.
/// </summary>
public sealed class FirstPassOptimizer
{
    private static readonly TimeSpan DuplicateClickWindow = TimeSpan.FromMilliseconds(650);

    public FirstPassOptimizationResult Optimize(WorkflowSynthesisDraft draft, int inputEventCount)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var output = new List<WorkflowStep>();
        var changes = ImmutableArray.CreateBuilder<RevisionChange>();
        var findings = draft.Findings.ToBuilder();
        var duplicateCount = 0;

        foreach (var candidate in draft.Steps.OrderBy(step => step.Order))
        {
            if (output.Count > 0 && IsDuplicateClick(output[^1], candidate))
            {
                var previous = output[^1];
                var evidence = previous.Evidence
                    .AddRange(candidate.Evidence)
                    .DistinctBy(item => item.CaptureEventId)
                    .ToImmutableArray();

                output[^1] = previous with
                {
                    Evidence = evidence,
                    Rationale = "Repeated click evidence was merged because the same semantic target was clicked within a short interval without an intervening workflow action."
                };

                duplicateCount++;
                changes.Add(new RevisionChange(
                    previous.Id,
                    RevisionChangeKind.Merged,
                    "Merged a repeated click on the same target.",
                    "Same action and semantic target within the duplicate-click window, with no intervening action.",
                    0.93,
                    evidence.Select(item => item.CaptureEventId).ToImmutableArray()));
                findings.Add(new OptimizationFinding(
                    "DuplicateClickMerged",
                    OptimizationSeverity.Information,
                    $"A repeated click on {previous.Title} was merged.",
                    evidence.Select(item => item.CaptureEventId).ToImmutableArray(),
                    0.93));
                continue;
            }

            if (output.Count > 0 && IsDuplicateApplicationSwitch(output[^1], candidate))
            {
                duplicateCount++;
                changes.Add(new RevisionChange(
                    output[^1].Id,
                    RevisionChangeKind.Merged,
                    "Merged a repeated application switch.",
                    "The same application was activated in adjacent workflow steps with no intervening action.",
                    0.95,
                    candidate.Evidence.Select(item => item.CaptureEventId).ToImmutableArray()));
                findings.Add(new OptimizationFinding(
                    "DuplicateApplicationSwitchMerged",
                    OptimizationSeverity.Information,
                    $"Repeated switch to {candidate.Target?.Application ?? "the same application"} was removed.",
                    candidate.Evidence.Select(item => item.CaptureEventId).ToImmutableArray(),
                    0.95));
                continue;
            }

            output.Add(candidate);
        }
        var normalized = output
            .Select((step, index) => step with { Order = index + 1 })
            .ToImmutableArray();

        var noiseCount = findings.Count(item =>
            item.Code is "UnanchoredDelayRemoved" or "NoiseEventRemoved" or "CursorMovementIgnored" or "UnpairedClipboardCopyRemoved");

        var clipboardCount = findings.Count(item => item.Code == "ClipboardTransferCorrelated");

        var optimizedDraft = draft with
        {
            Steps = normalized,
            Findings = findings.ToImmutable()
        };

        var report = new FirstPassOptimizationReport(
            inputEventCount,
            draft.Steps.Length,
            normalized.Length,
            noiseCount,
            duplicateCount,
            clipboardCount,
            findings.ToImmutable());

        return new FirstPassOptimizationResult(optimizedDraft, changes.ToImmutable(), report);
    }

    private static bool IsDuplicateApplicationSwitch(WorkflowStep previous, WorkflowStep current)
    {
        return previous.Action == WorkflowActionKind.SwitchApplication &&
               current.Action == WorkflowActionKind.SwitchApplication &&
               string.Equals(
                   previous.Target?.Application,
                   current.Target?.Application,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDuplicateClick(WorkflowStep previous, WorkflowStep current)
    {
        if (previous.Action != WorkflowActionKind.Click ||
            current.Action != WorkflowActionKind.Click ||
            previous.Target != current.Target)
        {
            return false;
        }

        var previousTime = previous.Evidence.IsDefaultOrEmpty ? (DateTimeOffset?)null : previous.Evidence[^1].OccurredAtUtc;
        var currentTime = current.Evidence.IsDefaultOrEmpty ? (DateTimeOffset?)null : current.Evidence[0].OccurredAtUtc;

        return previousTime is not null &&
               currentTime is not null &&
               currentTime >= previousTime &&
               currentTime - previousTime <= DuplicateClickWindow;
    }
}
