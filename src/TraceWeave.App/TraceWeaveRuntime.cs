using System.IO;
using System.Net.Http;using System.Collections.Immutable;
using TraceWeave.Core.Capture;
using TraceWeave.Core.Editing;
using TraceWeave.Core.Generation;
using TraceWeave.Core.Optimization;
using TraceWeave.Core.Review;
using TraceWeave.Core.Workflows;
using TraceWeave.Infrastructure.AI;
using TraceWeave.Infrastructure.Export;
using TraceWeave.Infrastructure.Storage;
using TraceWeave.Infrastructure.Windows;
using System.Windows.Threading;

namespace TraceWeave.App;

public sealed class TraceWeaveRuntime : IAsyncDisposable
{
    private readonly SqliteTraceWeaveStore _store;
    private readonly WindowsCaptureCoordinator _capture;
    private readonly FirstPassWorkflowGenerator _generator;
    private readonly WorkflowRevisionEditor _editor = new();
    private readonly RuleBasedWorkflowReviewProvider _localReviewer = new();
    private readonly WorkflowExporter _exporter = new();

    public TraceWeaveRuntime(Dispatcher dispatcher)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TraceWeave");

        Directory.CreateDirectory(dataDirectory);

        _store = new SqliteTraceWeaveStore(Path.Combine(dataDirectory, "traceweave.db"));
        _capture = new WindowsCaptureCoordinator(
            _store,
            dispatcher,
            new PatternTextRedactor());

        _generator = new FirstPassWorkflowGenerator(
            new RuleBasedWorkflowSynthesisProvider(),
            new TraceWeave.Core.Optimization.FirstPassOptimizer());

        _capture.EventObserved += eventData => EventObserved?.Invoke(eventData);
        _capture.StateChanged += (recording, paused) => StateChanged?.Invoke(recording, paused);
        _capture.PersistenceError += error => PersistenceError?.Invoke(error);
    }

    public bool IsRecording => _capture.IsRecording;
    public bool IsPaused => _capture.IsPaused;
    public Guid? CurrentRecordingId { get; private set; }
    public WorkflowDocument? CurrentWorkflow { get; private set; }
    public WorkflowReviewResult? LastReview { get; private set; }

    public event Action<CaptureEvent>? EventObserved;
    public event Action<bool, bool>? StateChanged;
    public event Action<Exception>? PersistenceError;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _store.InitializeAsync(cancellationToken);

    public async Task StartRecordingAsync(
        CaptureOptions options,
        CancellationToken cancellationToken = default)
    {
        await _store.InitializeAsync(cancellationToken);
        CurrentWorkflow = null;
        LastReview = null;
        CurrentRecordingId = Guid.NewGuid();
        await _capture.StartAsync(CurrentRecordingId.Value, options, cancellationToken);
    }

    public void PauseRecording() => _capture.Pause();
    public void ResumeRecording() => _capture.Resume();

    public async Task<FirstPassGenerationResult?> StopRecordingAndGenerateAsync(
        string? userIntent,
        CancellationToken cancellationToken = default)
    {
        if (CurrentRecordingId is not { } recordingId)
        {
            return null;
        }

        await _capture.StopAsync();

        var events = await _store.ReadRecordingAsync(recordingId, cancellationToken);
        var request = new WorkflowSynthesisRequest(
            recordingId,
            string.IsNullOrWhiteSpace(userIntent) ? null : userIntent.Trim(),
            events.ToImmutableArray());

        var result = await _generator.GenerateFirstPassAsync(request, cancellationToken);
        CurrentWorkflow = result.Workflow;
        await _store.SaveAsync(CurrentWorkflow, cancellationToken);
        return result;
    }

    public async Task<WorkflowReviewResult> ReviewAsync(
        bool useRemoteAi,
        string endpoint,
        string model,
        string apiKey,
        string? instruction,
        CancellationToken cancellationToken = default)
    {
        var workflow = CurrentWorkflow
            ?? throw new InvalidOperationException("There is no workflow to review.");

        var request = new WorkflowReviewRequest(
            workflow,
            workflow.CurrentRevision.Id,
            instruction,
            IncludeScreenshots: false,
            IncludeClipboardEvidence: false);

        IWorkflowReviewProvider provider = _localReviewer;
        if (useRemoteAi)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            provider = new OpenAiCompatibleWorkflowReviewProvider(client, endpoint, model, apiKey);
        }

        LastReview = await provider.ReviewAsync(request, cancellationToken);
        return LastReview;
    }

    public async Task ApplyReviewAsync(
        IEnumerable<Guid> proposalIds,
        CancellationToken cancellationToken = default)
    {
        if (CurrentWorkflow is null || LastReview is null)
        {
            throw new InvalidOperationException("There is no pending AI review.");
        }

        CurrentWorkflow = _editor.ApplyAiProposals(CurrentWorkflow, LastReview, proposalIds);
        await _store.SaveAsync(CurrentWorkflow, cancellationToken);
    }

    public async Task EditStepAsync(
        Guid stepId,
        string title,
        string intent,
        string? inputTemplate,
        string? shortcut,
        CancellationToken cancellationToken = default)
    {
        var workflow = CurrentWorkflow
            ?? throw new InvalidOperationException("There is no workflow to edit.");

        var current = workflow.CurrentRevision.Steps.Single(step => step.Id == stepId);
        CurrentWorkflow = _editor.UpdateStep(
            workflow,
            stepId,
            current with
            {
                Title = string.IsNullOrWhiteSpace(title) ? current.Title : title.Trim(),
                Intent = string.IsNullOrWhiteSpace(intent) ? current.Intent : intent.Trim(),
                InputTemplate = string.IsNullOrWhiteSpace(inputTemplate) ? null : inputTemplate.Trim(),
                Shortcut = string.IsNullOrWhiteSpace(shortcut) ? null : shortcut.Trim()
            },
            "Edited from the TraceWeave workflow editor.");

        await _store.SaveAsync(CurrentWorkflow, cancellationToken);
    }

    public async Task RemoveStepAsync(Guid stepId, CancellationToken cancellationToken = default)
    {
        var workflow = CurrentWorkflow
            ?? throw new InvalidOperationException("There is no workflow to edit.");

        CurrentWorkflow = _editor.RemoveStep(workflow, stepId, "Removed from the TraceWeave workflow editor.");
        await _store.SaveAsync(CurrentWorkflow, cancellationToken);
    }

    public async Task MoveStepAsync(Guid stepId, int newOrder, CancellationToken cancellationToken = default)
    {
        var workflow = CurrentWorkflow
            ?? throw new InvalidOperationException("There is no workflow to edit.");

        CurrentWorkflow = _editor.MoveStep(workflow, stepId, newOrder, "Reordered from the TraceWeave workflow editor.");
        await _store.SaveAsync(CurrentWorkflow, cancellationToken);
    }

    public async Task RestoreRevisionAsync(Guid revisionId, CancellationToken cancellationToken = default)
    {
        var workflow = CurrentWorkflow
            ?? throw new InvalidOperationException("There is no workflow to restore.");

        var version = workflow.Revisions.Single(revision => revision.Id == revisionId).Version;
        CurrentWorkflow = _editor.RestoreRevision(workflow, revisionId, $"Restored revision {version} from workflow history.");
        await _store.SaveAsync(CurrentWorkflow, cancellationToken);
    }

    public async Task<WorkflowExportResult> ExportAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var workflow = CurrentWorkflow
            ?? throw new InvalidOperationException("There is no workflow to export.");

        return await _exporter.ExportAsync(workflow, directory, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _capture.DisposeAsync();
}
