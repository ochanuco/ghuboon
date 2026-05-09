namespace Ghuboon.Infrastructure.GitHub;

/// <summary>
/// Converts GitHub REST API subject URLs (e.g. <c>https://api.github.com/repos/.../pulls/12</c>)
/// to their browser-facing equivalents (<c>https://github.com/.../pull/12</c>).
/// Returns null for inputs that cannot be safely converted; callers should fall back
/// to the repository html_url.
/// </summary>
internal static class SubjectUrlConverter
{
    private const string ApiHostPrefix = "https://api.github.com/repos/";
    private const string WebHost = "https://github.com/";

    public static string? ToWebUrl(string? subjectApiUrl, string? subjectType)
    {
        if (string.IsNullOrWhiteSpace(subjectApiUrl))
        {
            return null;
        }

        if (!subjectApiUrl.StartsWith(ApiHostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Strip the api host prefix; the remainder is "{owner}/{repo}/{kind}/{...}".
        var tail = subjectApiUrl.Substring(ApiHostPrefix.Length);
        var parts = tail.Split('/');
        if (parts.Length < 3)
        {
            return null;
        }

        var owner = parts[0];
        var repo = parts[1];
        var kind = parts[2];

        // Map API path segment -> web path segment.
        // pulls -> pull, issues -> issues, releases -> releases, commits -> commit.
        var webKind = kind switch
        {
            "pulls" => "pull",
            "issues" => "issues",
            "releases" => "releases",
            "commits" => "commit",
            "discussions" => "discussions",
            _ => kind,
        };

        // Rebuild as github.com/{owner}/{repo}/{webKind}/{rest...}
        if (parts.Length == 3)
        {
            // No identifier; safest is the repo page.
            return $"{WebHost}{owner}/{repo}";
        }

        var rest = string.Join('/', parts, 3, parts.Length - 3);
        // If the API URL is for "/pulls" (list) without an id, fall back to repo.
        if (string.IsNullOrEmpty(rest))
        {
            return $"{WebHost}{owner}/{repo}";
        }

        // Subject type may further refine; but kind mapping is already enough for MVP.
        _ = subjectType;
        return $"{WebHost}{owner}/{repo}/{webKind}/{rest}";
    }
}
