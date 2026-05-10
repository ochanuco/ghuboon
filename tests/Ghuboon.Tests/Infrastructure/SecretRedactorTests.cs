using Ghuboon.Infrastructure.Logging;

namespace Ghuboon.Tests.Infrastructure;

public class SecretRedactorTests
{
    [Theory]
    [InlineData("Authorization: token ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Authorization: ")]
    [InlineData("authorization: Bearer abc.def.ghi", "authorization: ")]
    [InlineData("AUTHORIZATION:    Bearer xyz", "AUTHORIZATION:    ")]
    [InlineData("Authorization:Bearer compact", "Authorization:")]
    // Header value containing internal whitespace must still be fully redacted —
    // the regex now captures the entire header value, not just the first token.
    [InlineData("Authorization: Bearer token with spaces inside", "Authorization: ")]
    // Non-ASCII characters in the value (e.g., a malformed token) must not slip
    // through as a residue.
    [InlineData("Authorization: Bearer tokén-wíth-unicode", "Authorization: ")]
    public void Redact_masks_authorization_header(string input, string expectedPrefix)
    {
        var output = SecretRedactor.Redact(input);

        // The full header value is replaced wholesale: the only thing left after the
        // colon (and any inter-token whitespace originally present after it) is the
        // [REDACTED] sentinel.
        Assert.Equal(expectedPrefix + SecretRedactor.Replacement, output);
        Assert.DoesNotContain("ghp_", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", output, StringComparison.Ordinal);
        Assert.DoesNotContain("token", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ghp_")]
    [InlineData("gho_")]
    [InlineData("ghu_")]
    [InlineData("ghs_")]
    [InlineData("ghr_")]
    public void Redact_masks_classic_pat_prefixes(string prefix)
    {
        // 36-char body satisfies the {36,255} pattern.
        var token = prefix + new string('A', 36);
        var input = $"using token {token} for request";

        var output = SecretRedactor.Redact(input);

        Assert.DoesNotContain(token, output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", output);
    }

    [Fact]
    public void Redact_masks_fine_grained_pat()
    {
        var token = "github_pat_" + new string('B', 40);
        var input = $"PAT={token}; trailing";

        var output = SecretRedactor.Redact(input);

        Assert.DoesNotContain(token, output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", output);
        Assert.Contains("trailing", output);
    }

    [Fact]
    public void Redact_leaves_non_secret_text_unchanged()
    {
        const string input = "fetched 12 notifications, etag W/\"abc123\", reason=mention";

        var output = SecretRedactor.Redact(input);

        Assert.Equal(input, output);
    }

    [Fact]
    public void Redact_handles_null_and_empty()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
        Assert.Equal(string.Empty, SecretRedactor.Redact(string.Empty));
    }

    [Fact]
    public void Redact_does_not_mask_short_lookalike_strings()
    {
        // ghp_short is too short for the classic-PAT pattern (needs 36+ body chars).
        const string input = "label gh_dummy and ghp_short";

        var output = SecretRedactor.Redact(input);

        Assert.Equal(input, output);
    }
}
