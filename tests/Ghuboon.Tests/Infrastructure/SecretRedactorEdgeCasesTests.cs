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

    /// <summary>
    /// Issue #30 (PR #28 follow-up): a payload with multiple
    /// <c>Authorization</c> headers that vary in casing, scheme, and inter-token
    /// whitespace must have every header value fully masked. The pinning test
    /// catches a regression where only the first match (or one canonical
    /// scheme) was redacted, leaving secrets in subsequent lines.
    /// </summary>
    [Fact]
    public void Redact_MultipleAuthorizationHeaders_VariedSchemesAndWhitespace_AllMasked()
    {
        // Mixed casing, three schemes (token / Bearer / OAuth-like custom),
        // varied inter-token whitespace, and one no-space "compact" header.
        const string input =
            "Authorization: token ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\r\n" +
            "authorization:    Bearer    eyJhbGciOiJIUzI1NiJ9.payload.sig\n" +
            "Some unrelated line with no auth header\n" +
            "AUTHORIZATION:Bearer compact-no-space-scheme\n" +
            "Authorization: token github_pat_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB\n" +
            "Authorization:\tCustomScheme keyId=\"x\", token=\"ghp_CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\"";

        var output = SecretRedactor.Redact(input);

        // No remnants of any token, scheme, or PAT body should leak through.
        Assert.DoesNotContain("ghp_", output, StringComparison.Ordinal);
        Assert.DoesNotContain("github_pat_", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", output, StringComparison.Ordinal);
        Assert.DoesNotContain("token ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", output, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomScheme", output, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", output, StringComparison.Ordinal);

        // All five Authorization lines should have produced one [REDACTED]
        // each — five redactions total. The non-auth line stays intact.
        var count = 0;
        var idx = 0;
        while ((idx = output.IndexOf(SecretRedactor.Replacement, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += SecretRedactor.Replacement.Length;
        }
        Assert.Equal(5, count);
        Assert.Contains("Some unrelated line with no auth header", output, StringComparison.Ordinal);
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
        // Issue #27: assert exactly 3 redactions (one per token). Allowing
        // ">= 3" hides over-redaction regressions where the engine could fire
        // multiple patterns on the same token region.
        var count = 0;
        var idx = 0;
        while ((idx = output.IndexOf(SecretRedactor.Replacement, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += SecretRedactor.Replacement.Length;
        }
        Assert.Equal(3, count);
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

        // Issue #27: this is a catastrophic-backtracking regression guard, not
        // a perf benchmark. The 1000 ms ceiling was tripping on slow CI runners
        // when the box was loaded. Bumped to 5000 ms — three orders of
        // magnitude over the typical local time (~5 ms). A real ReDoS would
        // blow past this comfortably.
        // Issue #42: use <= 5000 so a measurement that lands exactly on the
        // ceiling does not flip the assert. Stopwatch ticks are coarse enough
        // on loaded CI hosts that 5000 ms is a reachable measurement.
        Assert.True(sw.ElapsedMilliseconds <= 5000,
            $"redact took {sw.ElapsedMilliseconds} ms on 100KB input");
        Assert.DoesNotContain(token, output, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Replacement, output);
    }
}
