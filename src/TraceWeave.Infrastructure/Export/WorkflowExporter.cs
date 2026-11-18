using System.IO;using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Infrastructure.Export;

public sealed record WorkflowExportResult(
    string WorkflowJsonPath,
    string SkillMarkdownPath);

public sealed class WorkflowExporter
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<WorkflowExportResult> ExportAsync(
        WorkflowDocument workflow,
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("An export directory is required.", nameof(directory));
        }

        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);

        var safeName = MakeSafeFileName(workflow.Name);
        var jsonPath = Path.Combine(directory, $"{safeName}.workflow.json");
        var markdownPath = Path.Combine(directory, $"{safeName}.SKILL.md");

        var json = JsonSerializer.Serialize(workflow, _jsonOptions);
        await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8, cancellationToken);

        var markdown = BuildSkillMarkdown(workflow);
        await File.WriteAllTextAsync(markdownPath, markdown, Encoding.UTF8, cancellationToken);

        return new WorkflowExportResult(jsonPath, markdownPath);
    }

    private static string BuildSkillMarkdown(WorkflowDocument workflow)
    {
        var current = workflow.CurrentRevision;
        var builder = new StringBuilder();

        builder.AppendLine($"# {workflow.Name}");
        builder.AppendLine();
        builder.AppendLine(workflow.Goal);
        builder.AppendLine();
        builder.AppendLine($"> TraceWeave revision v{current.Version} · {current.Author} · {current.CreatedAtUtc:O}");
        builder.AppendLine();
        builder.AppendLine("## Preconditions");
        builder.AppendLine();

        var preconditions = current.Steps.SelectMany(step => step.Preconditions).Distinct().ToArray();
        if (preconditions.Length == 0)
        {
            builder.AppendLine("- None recorded.");
        }
        else
        {
            foreach (var item in preconditions) builder.AppendLine($"- {item}");
        }

        builder.AppendLine();
        builder.AppendLine("## Steps");
        builder.AppendLine();

        foreach (var step in current.Steps.OrderBy(step => step.Order))
        {
            builder.AppendLine($"### {step.Order}. {step.Title}");
            builder.AppendLine();
            builder.AppendLine($"- **Intent:** {step.Intent}");
            builder.AppendLine($"- **Action:** {step.Action}");

            if (!string.IsNullOrWhiteSpace(step.Shortcut))
            {
                builder.AppendLine($"- **Shortcut:** {step.Shortcut}");
            }

            if (!string.IsNullOrWhiteSpace(step.InputTemplate))
            {
                builder.AppendLine($"- **Input:** {step.InputTemplate}");
            }

            if (step.Target is not null)
            {
                if (!string.IsNullOrWhiteSpace(step.Target.Application)) builder.AppendLine($"- **Application:** {step.Target.Application}");
                if (!string.IsNullOrWhiteSpace(step.Target.AutomationId)) builder.AppendLine($"- **AutomationId:** {step.Target.AutomationId}");
                if (!string.IsNullOrWhiteSpace(step.Target.ElementName)) builder.AppendLine($"- **Element:** {step.Target.ElementName}");
                if (!string.IsNullOrWhiteSpace(step.Target.ControlType)) builder.AppendLine($"- **Control type:** {step.Target.ControlType}");
            }

            foreach (var condition in step.Postconditions)
            {
                builder.AppendLine($"- **Postcondition:** {condition}");
            }

            builder.AppendLine($"- **Confidence:** {step.Confidence:P0}");
            if (!string.IsNullOrWhiteSpace(step.Rationale))
            {
                builder.AppendLine($"- **Rationale:** {step.Rationale}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("## Execution rule");
        builder.AppendLine();
        builder.AppendLine("Prefer semantic UI Automation targets. If a precondition or postcondition fails, stop and report the state instead of blindly continuing.");
        return builder.ToString();
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "traceweave-workflow" : safe;
    }
}
