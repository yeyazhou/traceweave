using TraceWeave.Core.Optimization;

namespace TraceWeave.Core.Generation;

public interface IWorkflowGenerator
{
    Task<FirstPassGenerationResult> GenerateFirstPassAsync(
        WorkflowSynthesisRequest request,
        CancellationToken cancellationToken = default);
}
