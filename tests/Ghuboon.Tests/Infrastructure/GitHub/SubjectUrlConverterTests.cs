using Ghuboon.Infrastructure.GitHub;

namespace Ghuboon.Tests.Infrastructure.GitHub;

/// <summary>
/// Issue #11 follow-up: the converter must return null for kinds it cannot map
/// to a github.com web path so callers can fall back to <c>repository.html_url</c>
/// rather than emitting a fabricated URL.
/// </summary>
public class SubjectUrlConverterTests
{
    [Fact]
    public void Returns_null_for_unknown_kind()
    {
        // CheckSuite (and similar non-navigable subjects) sit on a path GitHub does not
        // expose at github.com/{owner}/{repo}/{kind}/{id}. Returning null lets the
        // mapper fall back to the repository html_url.
        var url = "https://api.github.com/repos/octo/hello/check-suites/12";
        var web = SubjectUrlConverter.ToWebUrl(url, "CheckSuite");
        Assert.Null(web);
    }

    [Fact]
    public void Returns_null_for_unknown_kind_without_id()
    {
        // Even when there is no id, an unmapped kind must still produce null
        // (previously returned the bare repo URL, which is acceptable but
        // we now consistently signal "fall back" via null for unknown kinds).
        var url = "https://api.github.com/repos/octo/hello/workflows";
        var web = SubjectUrlConverter.ToWebUrl(url, null);
        Assert.Null(web);
    }

    [Fact]
    public void Returns_null_for_non_api_host()
    {
        var url = "https://example.com/repos/octo/hello/issues/1";
        var web = SubjectUrlConverter.ToWebUrl(url, "Issue");
        Assert.Null(web);
    }

    [Fact]
    public void Maps_known_kinds_unchanged()
    {
        Assert.Equal(
            "https://github.com/octo/hello/pull/12",
            SubjectUrlConverter.ToWebUrl("https://api.github.com/repos/octo/hello/pulls/12", "PullRequest"));
        Assert.Equal(
            "https://github.com/octo/hello/issues/77",
            SubjectUrlConverter.ToWebUrl("https://api.github.com/repos/octo/hello/issues/77", "Issue"));
        Assert.Equal(
            "https://github.com/octo/hello/releases/v1",
            SubjectUrlConverter.ToWebUrl("https://api.github.com/repos/octo/hello/releases/v1", "Release"));
        Assert.Equal(
            "https://github.com/octo/hello/commit/abc123",
            SubjectUrlConverter.ToWebUrl("https://api.github.com/repos/octo/hello/commits/abc123", "Commit"));
        Assert.Equal(
            "https://github.com/octo/hello/discussions/9",
            SubjectUrlConverter.ToWebUrl("https://api.github.com/repos/octo/hello/discussions/9", "Discussion"));
    }
}
