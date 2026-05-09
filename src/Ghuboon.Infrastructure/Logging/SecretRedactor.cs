using System.Text.RegularExpressions;

namespace Ghuboon.Infrastructure.Logging;

/// <summary>
/// Masks secrets in arbitrary strings before they reach a log sink. Patterns cover Authorization
/// headers, classic GitHub PATs, and fine-grained GitHub PATs (ADR-012).
/// </summary>
public static partial class SecretRedactor
{
    public const string Replacement = "[REDACTED]";

    [GeneratedRegex(@"(?i)(Authorization\s*:\s*)\S+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    // Classic GitHub tokens: ghp_, gho_, ghu_, ghs_, ghr_ (PAT, OAuth, user, server, refresh).
    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9_]{36,255}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ClassicPatRegex();

    [GeneratedRegex(@"\bgithub_pat_[A-Za-z0-9_]{22,255}\b", RegexOptions.CultureInvariant)]
    private static partial Regex FineGrainedPatRegex();

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var result = AuthorizationHeaderRegex().Replace(input, $"$1{Replacement}");
        result = FineGrainedPatRegex().Replace(result, Replacement);
        result = ClassicPatRegex().Replace(result, Replacement);
        return result;
    }
}
