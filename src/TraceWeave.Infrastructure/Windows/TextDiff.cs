namespace TraceWeave.Infrastructure.Windows;

internal static class TextDiff
{
    public static string ExtractInsertedText(string previous, string current)
    {
        if (string.Equals(previous, current, StringComparison.Ordinal)) return string.Empty;
        if (string.IsNullOrEmpty(previous)) return current;
        if (string.IsNullOrEmpty(current)) return string.Empty;

        var prefix = 0;
        var maxPrefix = Math.Min(previous.Length, current.Length);
        while (prefix < maxPrefix && previous[prefix] == current[prefix])
        {
            prefix++;
        }

        var previousSuffix = previous.Length;
        var currentSuffix = current.Length;
        while (previousSuffix > prefix &&
               currentSuffix > prefix &&
               previous[previousSuffix - 1] == current[currentSuffix - 1])
        {
            previousSuffix--;
            currentSuffix--;
        }

        return current.Substring(prefix, currentSuffix - prefix);
    }
}
