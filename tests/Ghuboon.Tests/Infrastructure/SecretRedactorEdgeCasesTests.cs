using System;
using System.Diagnostics;
using System.Text;
using Ghuboon.Infrastructure.Logging;

namespace Ghuboon.Tests.Infrastructure;

/// <summary>
/// Phase 15: edge-case coverage for <see cref="SecretRedactor"/> beyond the
/// happy-path patterns. Catches regressions where a single match per pattern
/// might leak secondary occurrences in the same blob.
/// </summary>
public class SecretRedactorEdgeCasesTests
{
    [Fact]
    public void Redact_MultipleAuthorizationHeaders_AllMasked()
    {
        const string input =
            "Authorization: token ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n" +
            "Some unrelated line\n" +
            "Authorization: Bearer abc.def.ghi";

        var output = SecretRedactor.Redact(input);

        Assert.DoesNotContain("ghp_", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer abc", output, StringComparison.Ordinal);
        // Replacement appears for both headers.
        var firstIdx = output.IndexOf(SecretRedactor.Replacement, StringComparison.Ordinal);
        Assert.True(firstIdx >= 0);
        var secondIdx = output.IndexOf(SecretRedactor.Replacement, firstIdx + 1, StringComparison.Ordinal);
        Assert.True(secondIdx >= 0, "expected two redactions, found one");
    }

    [Fact]
    public void Redact_MultiplePatsInSameBlob_AllMasked()
    {
        var t1 = "ghp_" + new string('A', 36);
        var t2 = "github_pat_" + new string('B', 30);
        var t3 = "gho_" + new string('C', 40);
        var input = $"first={t1} second={t2} third={t3}";

        var output = SecretRedactor.Redact(input);

        Assert.DoesNotContain(t1, output, StringComparison.Ordinal);
        Assert.DoesNotContain(t2, output, StringComparison.Ordinal);
        Assert.DoesNotContain(t3, output, StringComparison.Ordinal);
        // Each token should have produced a [REDACTED] replacement.
        var count = 0;
        var idx = 0;
        while ((idx = output.IndexOf(SecretRedactor.Replacement, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += SecretRedactor.Replacement.Length;
        }
        Assert.True(count >= 3, $"expected at least 3 redactions, got {count}");
    }

    [Fact]
    public void Redact_PatAdjacentToTextWithoutSpace_StillMaskedWhenWordBoundaryPresent()
    {
        // Word boundary lives between '=' and 'g', and between the trailing
        // alphanumeric run and ';'. So `\b` matches and the regex fires.
        var token = "ghp_" + new string('A', 36);
        var input = $"PAT={token};suffix";

        var output = SecretRedactor.Redact(input);

        Assert.DoesNotContain(token, output, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Replacement, output);
        // The non-secret bookends are preserved.
        Assert.Contains("PAT=", output);
        Assert.Contains(";suffix", output);
    }

    [Fact]
    public void Redact_WhitespaceOnly_ReturnsInputUnchanged()
    {
        // Phase 15 invariant: the redactor must not mangle pure-whitespace blobs.
        const string input = "   \t\n  ";
        var output = SecretRedactor.Redact(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Redact_VeryLargeInput_CompletesQuickly_AndStillMasksSecret()
    {
        // 100 KB of innocuous text with a single PAT in the middle.
        var token = "ghp_" + new string('A', 36);
        var sb = new StringBuilder(110_000);
        sb.Append('x', 50_000);
        sb.Append(' ');
        sb.Append(token);
        sb.Append(' ');
        sb.Append('y', 50_000);
        var input = sb.ToString();

        var sw = Stopwatch.StartNew();
        var output = SecretRedactor.Redact(input);
        sw.Stop();

        // Generous budget; this is a regression sanity check, not a perf assertion.
        // A linear-time regex on 100 KB should comfortably finish well under 1s.
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"redact took {sw.ElapsedMilliseconds} ms on 100KB input");
        Assert.DoesNotContain(token, output, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Replacement, output);
    }
}
