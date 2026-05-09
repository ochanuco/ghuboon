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
        if (!string.IsNullOrEmpty(request.IfNoneMatch))
        {
            httpRequest.Headers.IfNoneMatch.ParseAdd(request.IfNoneMatch);
        }

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        var rateLimit = ParseRateLimit(response);
        var etag = response.Headers.ETag?.Tag;

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

    public async Task MarkThreadReadAsync(string pat, string threadId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pat);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        _log.Information("Marking thread {ThreadId} as read", threadId);

        using var request = BuildRequest(HttpMethod.Patch, $"notifications/threads/{Uri.EscapeDataString(threadId)}", pat);
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
                resetAt = DateTimeOffset.FromUnixTimeSeconds(unix);
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
}
