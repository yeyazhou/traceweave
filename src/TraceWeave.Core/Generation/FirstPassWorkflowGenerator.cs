using System.Collections.Immutable;
using TraceWeave.Core.Optimization;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Core.Generation;

public sealed class FirstPassWorkflowGenerator : IWorkflowGenerator
{
    private readonly IWorkflowSynthesisProvider _synthesisProvider;
    private readonly FirstPassOptimizer _optimizer;

    public FirstPassWorkflowGenerator(
        IWorkflowSynthesisProvider synthesisProvider,
        FirstPassOptimizer optimizer)
    {
        _synthesisProvider = synthesisProvider;
        _optimizer = optimizer;
    }

    public async Task<FirstPassGenerationResult> GenerateFirstPassAsync(
        WorkflowSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        var draft = await _synthesisProvider.SynthesizeAsync(request, cancellationToken);
        var optimized = _optimizer.Optimize(draft, request.Events.Length);

        var workflowId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var confidence = optimized.Draft.Steps.IsDefaultOrEmpty
            ? 0
            : optimized.Draft.Steps.Average(step => step.Confidence);

        var revision = new WorkflowRevision(
            revisionId,
            workflowId,
            1,
            null,
            DateTimeOffset.UtcNow,
            "TraceWeave First Pass",
            WorkflowRevisionKind.FirstPass,
            "Initial workflow generated and automatically optimized from captured evidence.",
            optimized.Draft.Steps,
            optimized.Changes,
            confidence);

        var workflow = new WorkflowDocument(
            workflowId,
            optimized.Draft.Name,
            optimized.Draft.Goal,
            DateTimeOffset.UtcNow,
            optimized.Draft.Parameters,
            ImmutableArray.Create(revision));

        return new FirstPassGenerationResult(workflow, optimized.Report);
    }
}
