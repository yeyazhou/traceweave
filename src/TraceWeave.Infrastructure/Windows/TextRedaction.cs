using System.Text.RegularExpressions;
using TraceWeave.Core.Capture;

namespace TraceWeave.Infrastructure.Windows;

public sealed record RedactionResult(
    string? Value,
    TextCaptureDisposition Disposition,
    SensitivityLevel Sensitivity,
    string? Reason = null);

public interface ITextRedactor
{
    RedactionResult Inspect(string value, string? processName = null, string? windowTitle = null);
}

/// <summary>
/// Conservative local detector. It never sends candidate text to a remote service.
/// False positives are preferred over persisting obvious credentials.
/// </summary>
public sealed partial class PatternTextRedactor : ITextRedactor
{
    public RedactionResult Inspect(string value, string? processName = null, string? windowTitle = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new RedactionResult(value, TextCaptureDisposition.Captured, SensitivityLevel.Public);
        }

        if (value.Length > 16_384)
        {
            return new RedactionResult(
                null,
                TextCaptureDisposition.Suppressed,
                SensitivityLevel.Confidential,
                "Text exceeded the configured capture size limit.");
        }

        if (PrivateKeyPattern().IsMatch(value) ||
            AuthorizationPattern().IsMatch(value) ||
            TokenPattern().IsMatch(value) ||
            AssignmentSecretPattern().IsMatch(value))
        {
            return new RedactionResult(
                null,
                TextCaptureDisposition.Redacted,
                SensitivityLevel.Secret,
                "Secret-like value detected by local pattern rules.");
        }

        if (EmailPattern().IsMatch(value))
        {
            return new RedactionResult(value, TextCaptureDisposition.Captured, SensitivityLevel.Personal);
        }

        return new RedactionResult(value, TextCaptureDisposition.Captured, SensitivityLevel.Public);
    }

    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex("\\b(?:Bearer|Basic)\\s+[A-Za-z0-9._~+/-]{16,}={0,2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex("\\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{16,}|m2a_[A-Za-z0-9_-]{16,})\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex("\\b(?:password|passwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token)\\s*[:=]\\s*\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentSecretPattern();

    [GeneratedRegex("\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}
