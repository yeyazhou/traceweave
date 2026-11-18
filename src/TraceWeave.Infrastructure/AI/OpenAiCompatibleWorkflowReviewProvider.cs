using System.Net.Http;
using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TraceWeave.Core.Review;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Infrastructure.AI;

/// <summary>
/// Optional OpenAI-compatible second-pass reviewer. The provider receives the derived workflow,
/// not the raw event journal. The response is validated and converted into explicit proposals.
/// </summary>
public sealed class OpenAiCompatibleWorkflowReviewProvider : IWorkflowReviewProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;

    public OpenAiCompatibleWorkflowReviewProvider(
        HttpClient httpClient,
        string endpoint,
        string model,
        string apiKey)
    {
        _httpClient = httpClient;
        _endpoint = endpoint.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
    }

    public async Task<WorkflowReviewResult> ReviewAsync(
        WorkflowReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.RevisionId != request.Workflow.CurrentRevision.Id)
        {
            throw new InvalidOperationException("The requested review revision is stale.");
        }

        if (string.IsNullOrWhiteSpace(_endpoint) ||
            string.IsNullOrWhiteSpace(_model) ||
            string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AI review is not configured. Set an endpoint, model, and API key.");
        }

        var knownEvidence = request.Workflow.CurrentRevision.Steps
            .SelectMany(step => step.Evidence)
            .Select(evidence => evidence.CaptureEventId)
            .ToHashSet();

        var workflowJson = JsonSerializer.Serialize(
            request.Workflow.CurrentRevision,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            });

        var systemPrompt = """
            You are TraceWeave's advanced workflow reviewer.
            Review a desktop workflow revision for correctness, robustness, unnecessary actions,
            missing actions, weak selectors, missing preconditions/postconditions, and opportunities
            to simplify the execution path.

            Return ONLY JSON with this shape:
            {
              "summary": "string",
              "findings": [
                {
                  "kind": "IncorrectOrder|MissingStep|RedundantStep|InvalidValue|Contradiction|UnsupportedInference|WeakVerification|FragileSelector|MissingPrecondition|MissingPostcondition|SecurityRisk|SimplificationOpportunity",
                  "severity": "Information|Warning|Error|Critical",
                  "title": "string",
                  "explanation": "string",
                  "confidence": 0.0,
                  "evidenceEventIds": ["guid"],
                  "relatedStepId": "guid|null"
                }
              ],
              "proposals": [
                {
                  "kind": "RemoveStep|MoveStep|ReplaceStep|InsertStep",
                  "targetStepId": "guid|null",
                  "newOrder": 1,
                  "reason": "string",
                  "confidence": 0.0,
                  "evidenceEventIds": ["guid"],
                  "replacement": null,
                  "inserted": null
                }
              ]
            }

            Never invent evidence IDs or step IDs. Only reference IDs present in the supplied workflow.
            A proposal is a suggestion, not an instruction to execute. Prefer small, evidence-backed changes.
            If the workflow is already good, return an empty proposals array.
            """;

        var userPrompt = $"""
            User instruction: {request.AdditionalInstruction ?? "(none)"}

            Workflow revision:
            {workflowJson}
            """;

        using var message = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(_endpoint));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        message.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = _model,
                temperature = 0.1,
                response_format = new { type = "json_object" },
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var envelope = JsonSerializer.Deserialize<ChatResponse>(
            responseBody,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var content = envelope?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("The AI provider returned no review content.");
        }

        var json = StripCodeFence(content);
        var review = JsonSerializer.Deserialize<RemoteReviewEnvelope>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The AI review JSON could not be parsed.");

        var findings = review.Findings
            .Select(item => ToFinding(item, knownEvidence, request.Workflow))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToImmutableArray();

        var proposals = review.Proposals
            .Select(item => ToProposal(item, knownEvidence, request.Workflow))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToImmutableArray();

        return new WorkflowReviewResult(
            request.Workflow.Id,
            request.RevisionId,
            "OpenAI-compatible",
            _model,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(review.Summary) ? "AI review completed." : review.Summary,
            findings,
            proposals);
    }

    private static WorkflowReviewFinding? ToFinding(
        RemoteFinding item,
        HashSet<Guid> knownEvidence,
        WorkflowDocument workflow)
    {
        if (!Enum.TryParse<WorkflowFindingKind>(item.Kind, true, out var kind) ||
            !Enum.TryParse<FindingSeverity>(item.Severity, true, out var severity))
        {
            return null;
        }

        var evidence = ValidEvidence(item.EvidenceEventIds, knownEvidence);
        var relatedStepId = ValidStepId(item.RelatedStepId, workflow);
        var confidence = Math.Clamp(item.Confidence, 0, 1);

        return new WorkflowReviewFinding(
            Guid.NewGuid(),
            kind,
            severity,
            item.Title ?? "AI finding",
            item.Explanation ?? string.Empty,
            confidence,
            evidence,
            relatedStepId);
    }

    private static WorkflowChangeProposal? ToProposal(
        RemoteProposal item,
        HashSet<Guid> knownEvidence,
        WorkflowDocument workflow)
    {
        if (!Enum.TryParse<ProposedChangeKind>(item.Kind, true, out var kind))
        {
            return null;
        }

        var targetStepId = ValidStepId(item.TargetStepId, workflow);
        var evidence = ValidEvidence(item.EvidenceEventIds, knownEvidence);
        var confidence = Math.Clamp(item.Confidence, 0, 1);

        if (kind is ProposedChangeKind.RemoveStep or ProposedChangeKind.MoveStep && targetStepId is null)
        {
            return null;
        }

        WorkflowStep? replacement = null;
        WorkflowStep? inserted = null;

        if (kind == ProposedChangeKind.ReplaceStep)
        {
            if (targetStepId is null || item.Replacement is null) return null;
            var original = workflow.CurrentRevision.Steps.SingleOrDefault(step => step.Id == targetStepId);
            if (original is null) return null;
            replacement = FromRemoteStep(item.Replacement, original.Id, original.Order, original);
        }

        if (kind == ProposedChangeKind.InsertStep)
        {
            if (item.Inserted is null) return null;
            inserted = FromRemoteStep(item.Inserted, Guid.NewGuid(), item.NewOrder ?? (workflow.CurrentRevision.Steps.Length + 1), null);
        }

        return new WorkflowChangeProposal(
            Guid.NewGuid(),
            kind,
            targetStepId,
            replacement,
            inserted,
            item.NewOrder,
            item.Reason ?? "AI-proposed workflow improvement.",
            confidence,
            evidence);
    }

    private static WorkflowStep FromRemoteStep(
        RemoteStep step,
        Guid id,
        int order,
        WorkflowStep? fallback)
    {
        var action = Enum.TryParse<WorkflowActionKind>(step.Action, true, out var parsedAction)
            ? parsedAction
            : fallback?.Action ?? WorkflowActionKind.Other;

        var target = step.Target is null
            ? fallback?.Target
            : new WorkflowTarget(
                step.Target.Application,
                step.Target.WindowTitlePattern,
                step.Target.AutomationId,
                step.Target.ElementName,
                step.Target.ControlType,
                step.Target.FallbackDescription);

        var evidence = fallback?.Evidence ?? ImmutableArray<EvidenceReference>.Empty;

        return new WorkflowStep(
            id,
            order,
            step.Title ?? fallback?.Title ?? "AI-proposed step",
            step.Intent ?? fallback?.Intent ?? "Perform the proposed workflow action.",
            action,
            target,
            step.InputTemplate ?? fallback?.InputTemplate,
            step.Shortcut ?? fallback?.Shortcut,
            step.Preconditions?.ToImmutableArray() ?? fallback?.Preconditions ?? ImmutableArray<string>.Empty,
            step.Postconditions?.ToImmutableArray() ?? fallback?.Postconditions ?? ImmutableArray<string>.Empty,
            evidence,
            step.IsRequired ?? fallback?.IsRequired ?? true,
            StepOrigin.AiProposed,
            StepReviewStatus.NeedsReview,
            Math.Clamp(step.Confidence ?? fallback?.Confidence ?? 0.5, 0, 1),
            step.Rationale ?? "Proposed by the advanced AI reviewer.");
    }

    private static ImmutableArray<Guid> ValidEvidence(
        IEnumerable<Guid>? ids,
        HashSet<Guid> knownEvidence) =>
        (ids ?? [])
            .Where(knownEvidence.Contains)
            .Distinct()
            .ToImmutableArray();

    private static Guid? ValidStepId(Guid? id, WorkflowDocument workflow) =>
        id is { } value && workflow.CurrentRevision.Steps.Any(step => step.Id == value)
            ? value
            : null;

    private static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("~~~", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
            {
                return trimmed[(firstNewLine + 1)..lastFence].Trim();
            }
        }

        return trimmed;
    }

    private static string BuildEndpoint(string endpoint) =>
        endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : $"{endpoint}/chat/completions";

    private sealed record ChatResponse(List<ChatChoice>? Choices);
    private sealed record ChatChoice(ChatMessage? Message);
    private sealed record ChatMessage(string? Content);

    private sealed record RemoteReviewEnvelope(
        string? Summary,
        List<RemoteFinding> Findings,
        List<RemoteProposal> Proposals)
    {
        public List<RemoteFinding> Findings { get; init; } = Findings ?? [];
        public List<RemoteProposal> Proposals { get; init; } = Proposals ?? [];
    }

    private sealed record RemoteFinding(
        string? Kind,
        string? Severity,
        string? Title,
        string? Explanation,
        double Confidence,
        List<Guid>? EvidenceEventIds,
        Guid? RelatedStepId);

    private sealed record RemoteProposal(
        string? Kind,
        Guid? TargetStepId,
        int? NewOrder,
        string? Reason,
        double Confidence,
        List<Guid>? EvidenceEventIds,
        RemoteStep? Replacement,
        RemoteStep? Inserted);

    private sealed record RemoteStep(
        string? Title,
        string? Intent,
        string? Action,
        WorkflowTargetDto? Target,
        string? InputTemplate,
        string? Shortcut,
        List<string>? Preconditions,
        List<string>? Postconditions,
        bool? IsRequired,
        double? Confidence,
        string? Rationale);

    private sealed record WorkflowTargetDto(
        string? Application,
        string? WindowTitlePattern,
        string? AutomationId,
        string? ElementName,
        string? ControlType,
        string? FallbackDescription);
}
