using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.GitHub.Dto;
using Ghuboon.Infrastructure.Logging;
using Serilog;

namespace Ghuboon.Infrastructure.GitHub;

/// <summary>
/// HttpClient-based implementation of <see cref="IGitHubApiClient"/> for GitHub.com (ADR-009, ADR-016).
/// Sends only the small subset of endpoints Ghuboon needs: <c>/user</c>, <c>/notifications</c>,
/// and <c>PATCH /notifications/threads/{id}</c>. Captures ETag and rate-limit headers.
/// Authorization headers and PAT values are never logged (ADR-007, ADR-012).
/// </summary>
public sealed class GitHubApiClient : IGitHubApiClient
{
    public const string DefaultBaseUrl = "https://api.github.com";
    public const string UserAgent = "Ghuboon/0.1";
    public const string AcceptMediaType = "application/vnd.github+json";
    public const string ApiVersion = "2022-11-28";
    public const string ApiVersionHeader = "X-GitHub-Api-Version";

    /// <summary>
    /// Allowlist of hosts that may receive an Authorization header carrying
    /// the PAT. Subject URLs come from cached notification payloads and
    /// could in theory be tampered with (DB rewrite) or drift to an
    /// unexpected origin (GHES vs GitHub.com), so we guard each absolute-URL
    /// fetch path against this set before sending the credential.
    /// </summary>
    private static readonly HashSet<string> AllowedAuthHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
    };

    private static bool IsAllowedAuthHost(Uri uri) =>
        uri is not null
        && uri.Scheme == Uri.UriSchemeHttps
        && AllowedAuthHosts.Contains(uri.Host);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger _log;

    public GitHubApiClient(HttpClient http, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(log);
        _http = http;
        _log = log.ForContext<GitHubApiClient>();

        // Set BaseAddress only if caller didn't already configure one (e.g. tests with mock handlers
        // typically pass a pre-configured HttpClient).
        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(DefaultBaseUrl);
        }
    }

    public async Task<UserValidationResult> ValidateAsync(string pat, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pat);

        _log.Information("Validating PAT against GET /user");

        try
        {
            using var request = BuildRequest(HttpMethod.Get, "user", pat);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var user = await response.Content.ReadFromJsonAsync<UserDto>(JsonOptions, ct).ConfigureAwait(false);
                _log.Information("PAT validation succeeded for login {Login}", user?.Login);
                return new UserValidationResult(true, user?.Login, null, null);
            }

            var bodyForDiagnosis = await ReadBodySafelyAsync(response, ct).ConfigureAwait(false);
            var (category, message) = MapErrorStatus(response, bodyForDiagnosis);
            _log.Warning("PAT validation failed: status {StatusCode} category {Category}", (int)response.StatusCode, category);
            return new UserValidationResult(false, null, category, message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _log.Error(ex, "Network error during PAT validation");
            return new UserValidationResult(false, null, ErrorCategory.Network, "Network error contacting GitHub.");
        }
        catch (TaskCanceledException ex)
        {
            // Timeout (not user cancellation, since we filtered above).
            _log.Error(ex, "Timeout during PAT validation");
            return new UserValidationResult(false, null, ErrorCategory.Network, "Request to GitHub timed out.");
        }
    }

    public async Task<NotificationsResponse> ListNotificationsAsync(string pat, NotificationsRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pat);
        ArgumentNullException.ThrowIfNull(request);

        var path = BuildNotificationsPath(request);
        _log.Information("Listing notifications all={All} participating={Participating} ifNoneMatch={HasEtag}",
            request.All, request.Participating, !string.IsNullOrEmpty(request.IfNoneMatch));

        using var httpRequest = BuildRequest(HttpMethod.Get, path, pat);
        // Skip the conditional header for "empty" etags. GitHub may have
        // previously responded with `etag: ""` (or `W/""`) which we then
        // stored — sending it back unconditionally returns 304 forever.
        var ifNoneMatch = request.IfNoneMatch;
        if (!string.IsNullOrWhiteSpace(ifNoneMatch)
            && !string.Equals(ifNoneMatch, "\"\"", StringComparison.Ordinal)
            && !string.Equals(ifNoneMatch, "W/\"\"", StringComparison.Ordinal))
        {
            httpRequest.Headers.IfNoneMatch.ParseAdd(ifNoneMatch);
        }

        try
        {
            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            var rateLimit = ParseRateLimit(response);
            // GitHub's notifications endpoint sometimes returns an empty
            // quoted etag (`etag: ""`). Persisting that and sending it back
            // as If-None-Match makes GitHub respond 304 forever even when
            // new threads arrive. Treat empty/whitespace-only etag tags as
            // "no etag" so the next sync omits the conditional header.
            var rawEtag = response.Headers.ETag?.Tag;
            var etag = string.IsNullOrWhiteSpace(rawEtag)
                || string.Equals(rawEtag, "\"\"", StringComparison.Ordinal)
                || string.Equals(rawEtag, "W/\"\"", StringComparison.Ordinal)
                ? null
                : rawEtag;

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                _log.Information("Notifications not modified (304); reusing cached etag");
                return new NotificationsResponse(Array.Empty<GitHubNotification>(), etag, rateLimit, true);
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var dtos = await response.Content
                    .ReadFromJsonAsync<List<NotificationDto>>(JsonOptions, ct)
                    .ConfigureAwait(false) ?? new List<NotificationDto>();

                var mapped = NotificationMapper.Map(dtos, request.AccountId);
                _log.Information("Fetched {Count} notifications", mapped.Count);
                return new NotificationsResponse(mapped, etag, rateLimit, false);
            }

            var body = await ReadBodySafelyAsync(response, ct).ConfigureAwait(false);
            var (category, message) = MapErrorStatus(response, body);
            _log.Warning("ListNotifications failed: status {StatusCode} category {Category}", (int)response.StatusCode, category);
            throw new GitHubApiException(category, message, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — propagate cleanly so SyncAsync/test code can observe OCE.
            throw;
        }
        catch (HttpRequestException ex)
        {
            _log.Warning(ex, "Network error during ListNotifications");
            throw new GitHubApiException(ErrorCategory.Network, ex.Message, 0, ex);
        }
        catch (TaskCanceledException ex)
        {
            // Timeout (not user cancellation, since we filtered above).
            _log.Warning(ex, "Timeout during ListNotifications");
            throw new GitHubApiException(ErrorCategory.Network, ex.Message, 0, ex);
        }
    }

    public async Task MarkThreadReadAsync(string pat, string threadId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pat);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        _log.Information("Marking thread {ThreadId} as read", threadId);

        using var request = BuildRequest(HttpMethod.Patch, $"notifications/threads/{Uri.EscapeDataString(threadId)}", pat);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // GitHub returns 205 Reset Content on success. Some proxies surface 200; accept both.
            if (response.StatusCode == HttpStatusCode.ResetContent || response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            var body = await ReadBodySafelyAsync(response, ct).ConfigureAwait(false);
            var (category, message) = MapErrorStatus(response, body);
            _log.Warning("MarkThreadRead failed: status {StatusCode} category {Category}", (int)response.StatusCode, category);
            throw new GitHubApiException(category, message, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — propagate cleanly.
            throw;
        }
        catch (HttpRequestException ex)
        {
            _log.Warning(ex, "Network error during MarkThreadRead");
            throw new GitHubApiException(ErrorCategory.Network, ex.Message, 0, ex);
        }
        catch (TaskCanceledException ex)
        {
            // Timeout (not user cancellation, since we filtered above).
            _log.Warning(ex, "Timeout during MarkThreadRead");
            throw new GitHubApiException(ErrorCategory.Network, ex.Message, 0, ex);
        }
    }

    public async Task<string?> GetSubjectBodyAsync(string pat, string subjectApiUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pat);
        if (string.IsNullOrWhiteSpace(subjectApiUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(subjectApiUrl, UriKind.Absolute, out var absolute))
        {
            return null;
        }

        // Refuse to send the Authorization header to any host other than the
        // GitHub API allowlist — subject URLs originate from cached payloads
        // and can drift / be tampered with, and the PAT must never reach a
        // third-party origin via that path.
        if (!IsAllowedAuthHost(absolute))
        {
            _log.Warning("GetSubjectBody rejected non-allowlisted host {Host}", absolute.Host);
            return null;
        }

        // Build a fresh request bypassing the BaseAddress; subject_url is a fully
        // qualified GitHub API URL pointing at /repos/{owner}/{repo}/{pulls|issues|...}/{n}
        // (or comment / commit / discussion variants).
        using var req = new HttpRequestMessage(HttpMethod.Get, absolute);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptMediaType));
        req.Headers.TryAddWithoutValidation(ApiVersionHeader, ApiVersion);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", pat);

        try
        {
            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.Information("GetSubjectBody non-success status {StatusCode}", (int)response.StatusCode);
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // PullRequest / Issue / Comment / Discussion all carry "body".
            // Release uses "body" too; commits use "commit.message" but we
            // surface only "body" for MVP simplicity.
            if (doc.RootElement.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
            {
                return body.GetString();
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Information(ex, "GetSubjectBody failed (non-fatal)");
            return null;
        }
    }

    public Task<string?> GetThreadSubjectUrlAsync(string pat, string threadId, CancellationToken ct = default)
        => GetThreadSubjectFieldAsync(pat, threadId, "url", ct);

    public async Task<(string? Body, string? AuthorLogin)> GetSubjectBodyAndAuthorAsync(string pat, string subjectApiUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pat);
        if (string.IsNullOrWhiteSpace(subjectApiUrl)) return (null, null);
        return await GetSubjectBodyAndUserAsync(pat, subjectApiUrl, ct).ConfigureAwait(false);
    }

    public async Task<(string? Body, string? AuthorLogin, DateTimeOffset? CreatedAt)> GetSubjectMetaAsync(string pat, string subjectApiUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pat);
        if (string.IsNullOrWhiteSpace(subjectApiUrl)) return (null, null, null);
        return await GetSubjectMetaInternalAsync(pat, subjectApiUrl, ct).ConfigureAwait(false);
    }

    private async Task<(string? Body, string? AuthorLogin, DateTimeOffset? CreatedAt)> GetSubjectMetaInternalAsync(string pat, string subjectApiUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(subjectApiUrl, UriKind.Absolute, out var absolute)) return (null, null, null);
        if (!IsAllowedAuthHost(absolute))
        {
            _log.Warning("GetSubjectMeta rejected non-allowlisted host {Host}", absolute.Host);
            return (null, null, null);
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, absolute);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptMediaType));
        req.Headers.TryAddWithoutValidation(ApiVersionHeader, ApiVersion);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", pat);
        try
        {
            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, null, null);
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null, null);
            string? body = null, login = null;
            DateTimeOffset? createdAt = null;
            if (doc.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String) body = b.GetString();
            if (doc.RootElement.TryGetProperty("user", out var u)
                && u.ValueKind == JsonValueKind.Object
                && u.TryGetProperty("login", out var l)
                && l.ValueKind == JsonValueKind.String)
            {
                login = l.GetString();
            }
            if (doc.RootElement.TryGetProperty("created_at", out var c)
                && c.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(c.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                createdAt = parsed;
            }
            return (body, login, createdAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Information(ex, "GetSubjectMeta failed (non-fatal)"); return (null, null, null); }
    }

    public async Task<(string? Body, string? AuthorLogin)> GetLatestCommentDetailsAsync(string pat, string threadId, CancellationToken ct = default)
    {
        var commentUrl = await GetThreadSubjectFieldAsync(pat, threadId, "latest_comment_url", ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(commentUrl)
            && commentUrl.Contains("/comments/", StringComparison.Ordinal))
        {
            var direct = await GetSubjectBodyAndUserAsync(pat, commentUrl, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(direct.Body)) return direct;
        }

        var subjectUrl = await GetThreadSubjectFieldAsync(pat, threadId, "url", ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(subjectUrl)) return (null, null);

        var commentsUrl = BuildIssueCommentsUrl(subjectUrl);
        if (commentsUrl is null) return (null, null);

        return await FetchLatestIssueCommentDetailsAsync(pat, commentsUrl, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetLatestCommentBodyAsync(string pat, string threadId, CancellationToken ct = default)
    {
        // First try the thread's own latest_comment_url. When GitHub points
        // it at a real comment ("/comments/<id>") we just fetch that and
        // return the body. When it falls back to the PR/Issue URL itself
        // ("/pulls/<n>" or "/issues/<n>" with no /comments/ segment), the
        // notification was bumped by a non-comment activity (push, review
        // summary, state change). In that case, fetch the issue-comments
        // endpoint for the most recently created comment so the detail
        // pane still shows comment content when one exists.
        var commentUrl = await GetThreadSubjectFieldAsync(pat, threadId, "latest_comment_url", ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(commentUrl)
            && commentUrl.Contains("/comments/", StringComparison.Ordinal))
        {
            var direct = await GetSubjectBodyAsync(pat, commentUrl, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(direct))
            {
                return direct;
            }
        }

        // Fall back: ask the issue's comments collection for the newest one.
        var subjectUrl = await GetThreadSubjectFieldAsync(pat, threadId, "url", ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(subjectUrl))
        {
            return null;
        }

        var commentsUrl = BuildIssueCommentsUrl(subjectUrl);
        if (commentsUrl is null)
        {
            return null;
        }

        return await FetchLatestIssueCommentBodyAsync(pat, commentsUrl, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Convert a PR/Issue subject URL into the issue-comments collection URL.
    /// <c>https://api.github.com/repos/o/r/pulls/31</c> →
    /// <c>https://api.github.com/repos/o/r/issues/31/comments</c>. PRs share
    /// the issues/<n>/comments endpoint for top-level comments. Returns
    /// null if the input doesn't match the expected shape.
    /// </summary>
    private static string? BuildIssueCommentsUrl(string subjectUrl)
    {
        if (!Uri.TryCreate(subjectUrl, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        // expected: repos/{owner}/{repo}/{pulls|issues}/{number}
        if (segments.Length != 5) return null;
        if (!string.Equals(segments[0], "repos", StringComparison.OrdinalIgnoreCase)) return null;
        if (!int.TryParse(segments[4], out _)) return null;

        var rebuilt = $"{uri.Scheme}://{uri.Host}/repos/{segments[1]}/{segments[2]}/issues/{segments[4]}/comments";
        return rebuilt;
    }

    private async Task<(string? Body, string? AuthorLogin)> GetSubjectBodyAndUserAsync(string pat, string subjectApiUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(subjectApiUrl, UriKind.Absolute, out var absolute)) return (null, null);
        if (!IsAllowedAuthHost(absolute))
        {
            _log.Warning("GetSubjectBodyAndUser rejected non-allowlisted host {Host}", absolute.Host);
            return (null, null);
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, absolute);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptMediaType));
        req.Headers.TryAddWithoutValidation(ApiVersionHeader, ApiVersion);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", pat);
        try
        {
            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, null);
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            string? body = null, login = null;
            if (doc.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String) body = b.GetString();
            if (doc.RootElement.TryGetProperty("user", out var u)
                && u.ValueKind == JsonValueKind.Object
                && u.TryGetProperty("login", out var l)
                && l.ValueKind == JsonValueKind.String)
            {
                login = l.GetString();
            }
            return (body, login);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Information(ex, "GetSubjectBodyAndUser failed (non-fatal)"); return (null, null); }
    }

    private async Task<(string? Body, string? AuthorLogin)> FetchLatestIssueCommentDetailsAsync(string pat, string commentsUrl, CancellationToken ct)
    {
        var withQuery = $"{commentsUrl}?per_page=1&sort=created&direction=desc";
        if (!Uri.TryCreate(withQuery, UriKind.Absolute, out var absolute)) return (null, null);
        if (!IsAllowedAuthHost(absolute))
        {
            _log.Warning("FetchLatestIssueCommentDetails rejected non-allowlisted host {Host}", absolute.Host);
            return (null, null);
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, absolute);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptMediaType));
        req.Headers.TryAddWithoutValidation(ApiVersionHeader, ApiVersion);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", pat);
        try
        {
            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, null);
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return (null, null);
            var first = doc.RootElement[0];
            if (first.ValueKind != JsonValueKind.Object) return (null, null);
            string? body = null, login = null;
            if (first.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String) body = b.GetString();
            if (first.TryGetProperty("user", out var u)
                && u.ValueKind == JsonValueKind.Object
                && u.TryGetProperty("login", out var l)
                && l.ValueKind == JsonValueKind.String)
            {
                login = l.GetString();
            }
            return (body, login);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Information(ex, "FetchLatestIssueCommentDetails failed (non-fatal)"); return (null, null); }
    }

    private async Task<string?> FetchLatestIssueCommentBodyAsync(string pat, string commentsUrl, CancellationToken ct)
    {
        var withQuery = $"{commentsUrl}?per_page=1&sort=created&direction=desc";
        if (!Uri.TryCreate(withQuery, UriKind.Absolute, out var absolute))
        {
            return null;
        }

        if (!IsAllowedAuthHost(absolute))
        {
            _log.Warning("FetchLatestIssueCommentBody rejected non-allowlisted host {Host}", absolute.Host);
            return null;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, absolute);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptMediaType));
        req.Headers.TryAddWithoutValidation(ApiVersionHeader, ApiVersion);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", pat);

        try
        {
            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.Information("FetchLatestIssueComment non-success status {StatusCode}", (int)response.StatusCode);
                return null;
            }
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }
            var first = doc.RootElement[0];
            if (first.ValueKind != JsonValueKind.Object) return null;
            if (first.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String)
            {
                return b.GetString();
            }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Information(ex, "FetchLatestIssueComment failed (non-fatal)"); return null; }
    }

    private async Task<string?> GetThreadSubjectFieldAsync(string pat, string threadId, string fieldName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pat);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        using var req = BuildRequest(HttpMethod.Get, $"notifications/threads/{Uri.EscapeDataString(threadId)}", pat);
        try
        {
            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.Information("GetThreadSubjectField {Field} non-success status {StatusCode}", fieldName, (int)response.StatusCode);
                return null;
            }
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty("subject", out var subj)
                && subj.ValueKind == JsonValueKind.Object
                && subj.TryGetProperty(fieldName, out var v)
                && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Information(ex, "GetThreadSubjectField {Field} failed (non-fatal)", fieldName); return null; }
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string relativePath, string pat)
    {
        var req = new HttpRequestMessage(method, relativePath);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptMediaType));
        req.Headers.TryAddWithoutValidation(ApiVersionHeader, ApiVersion);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", pat);
        return req;
    }

    private static string BuildNotificationsPath(NotificationsRequest request)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (request.All)
        {
            query["all"] = "true";
        }
        if (request.Participating)
        {
            query["participating"] = "true";
        }
        if (request.Since is not null)
        {
            // GitHub expects ISO 8601 / RFC3339, UTC.
            query["since"] = request.Since.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        var qs = query.ToString();
        return string.IsNullOrEmpty(qs) ? "notifications" : $"notifications?{qs}";
    }

    private static RateLimitInfo ParseRateLimit(HttpResponseMessage response)
    {
        int? remaining = null;
        DateTimeOffset? resetAt = null;

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues))
        {
            var raw = remainingValues.FirstOrDefault();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                remaining = parsed;
            }
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
        {
            var raw = resetValues.FirstOrDefault();
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            {
                // DateTimeOffset.FromUnixTimeSeconds throws ArgumentOutOfRangeException for
                // values outside [-62135596800, 253402300799]. Cheap pre-check + defensive
                // try/catch leaves resetAt = null on bad input rather than crashing the client
                // (issue #11).
                if (unix is >= 0 and <= 253_402_300_799L)
                {
                    try
                    {
                        resetAt = DateTimeOffset.FromUnixTimeSeconds(unix);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        resetAt = null;
                    }
                }
            }
        }

        return new RateLimitInfo(remaining, resetAt);
    }

    private static (ErrorCategory Category, string Message) MapErrorStatus(HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;

        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                return (ErrorCategory.Auth, "Token invalid or expired.");

            case HttpStatusCode.Forbidden:
            {
                // Rate limit?
                var rl = ParseRateLimit(response);
                if (rl.Remaining is 0)
                {
                    return (ErrorCategory.RateLimit, "GitHub API rate limit exceeded.");
                }

                if (LooksLikeSamlSso(body) || LooksLikeSamlSso(response.Headers))
                {
                    return (ErrorCategory.Auth,
                        "Organization requires SAML SSO authorization for this PAT. " +
                        "Authorize the token at https://github.com/settings/tokens and re-try.");
                }

                if (body.Contains("Resource not accessible by personal access token", StringComparison.OrdinalIgnoreCase))
                {
                    return (ErrorCategory.Auth, "PAT does not have access to this resource. Authorize the token or grant the required scope.");
                }

                return (ErrorCategory.Auth, "Forbidden by GitHub API.");
            }

            case HttpStatusCode.NotFound:
                return (ErrorCategory.ApiCompatibility, "GitHub API resource not found.");

            case HttpStatusCode.UnprocessableEntity:
                return (ErrorCategory.ApiCompatibility, "GitHub API rejected the request as unprocessable.");

            default:
                if (status >= 500 && status < 600)
                {
                    return (ErrorCategory.Network, $"GitHub API transient failure (HTTP {status}).");
                }
                return (ErrorCategory.Unknown, $"Unexpected GitHub API response (HTTP {status}).");
        }
    }

    private static bool LooksLikeSamlSso(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }
        return body.Contains("saml_sso", StringComparison.OrdinalIgnoreCase)
            || body.Contains("SAML enforcement", StringComparison.OrdinalIgnoreCase)
            || body.Contains("authorized for SAML SSO", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSamlSso(HttpResponseHeaders headers)
    {
        // GitHub adds X-GitHub-SSO header on SAML-protected resources.
        return headers.Contains("X-GitHub-SSO");
    }

    private async Task<string> ReadBodySafelyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Defense in depth: never let secrets leak through any indirect log of body content.
            return SecretRedactor.Redact(raw);
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// Thrown when a GitHub API call returns an unsuccessful status that the API client
/// converts to an <see cref="ErrorCategory"/>. Callers may inspect the category to
/// surface UI status (Phase 14).
/// </summary>
public sealed class GitHubApiException : Exception
{
    public ErrorCategory Category { get; }
    public int StatusCode { get; }

    public GitHubApiException(ErrorCategory category, string message, int statusCode)
        : base(message)
    {
        Category = category;
        StatusCode = statusCode;
    }

    public GitHubApiException(ErrorCategory category, string message, int statusCode, Exception? innerException)
        : base(message, innerException)
    {
        Category = category;
        StatusCode = statusCode;
    }
}
