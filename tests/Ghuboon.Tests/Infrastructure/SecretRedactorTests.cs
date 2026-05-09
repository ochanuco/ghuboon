using Ghuboon.Infrastructure.Logging;

namespace Ghuboon.Tests.Infrastructure;

public class SecretRedactorTests
{
    [Theory]
    [InlineData("Authorization: token ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("authorization: Bearer abc.def.ghi")]
    [InlineData("AUTHORIZATION:    Bearer xyz")]
    [InlineData("Authorization:Bearer compact")]
    public void Redact_masks_authorization_header(string input)
    {
        var output = SecretRedactor.Redact(input);

        Assert.Contains("[REDACTED]", output);
        Assert.DoesNotContain("ghp_", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer abc", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer xyz", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer compact", output, StringComparison.Ordinal);
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
