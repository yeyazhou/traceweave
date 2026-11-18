namespace TraceWeave.Core.Workflows;

public interface IWorkflowRepository
{
    Task SaveAsync(WorkflowDocument workflow, CancellationToken cancellationToken = default);

    Task<WorkflowDocument?> GetAsync(Guid workflowId, CancellationToken cancellationToken = default);
}
