using System.Collections.Immutable;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Core.Optimization;

public sealed record FirstPassOptimizationResult(
    WorkflowSynthesisDraft Draft,
    ImmutableArray<RevisionChange> Changes,
    FirstPassOptimizationReport Report);
