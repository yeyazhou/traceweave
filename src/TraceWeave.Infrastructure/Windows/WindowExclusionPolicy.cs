using System.IO;

namespace TraceWeave.Infrastructure.Windows;

public interface IWindowExclusionPolicy
{
    bool IsExcluded(string? processName, string? windowTitle);
}

public sealed class DefaultWindowExclusionPolicy : IWindowExclusionPolicy
{
    private static readonly HashSet<string> BuiltInExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "logonui",
        "winlogon",
        "credentialuibroker",
        "consent",
        "lsass",
        "keepass",
        "keepassxc",
        "1password",
        "bitwarden",
        "lastpass",
        "authy"
    };

    private readonly IReadOnlySet<string> _configuredProcesses;
    private readonly string[] _excludedTitleFragments;

    public DefaultWindowExclusionPolicy(
        IReadOnlySet<string>? configuredProcesses = null,
        IEnumerable<string>? excludedTitleFragments = null)
    {
        _configuredProcesses = configuredProcesses ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _excludedTitleFragments = (excludedTitleFragments ?? ["credential", "password manager", "private browsing"])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    public bool IsExcluded(string? processName, string? windowTitle)
    {
        if (!string.IsNullOrWhiteSpace(processName) &&
            (BuiltInExcludedProcesses.Contains(Path.GetFileNameWithoutExtension(processName)) ||
             _configuredProcesses.Contains(Path.GetFileNameWithoutExtension(processName))))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(windowTitle)) return false;
        return _excludedTitleFragments.Any(fragment =>
            windowTitle.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
