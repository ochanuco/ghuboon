using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ghuboon.Core.Abstractions;
using Ghuboon.Infrastructure.GitHub;
using Ghuboon.Infrastructure.Logging;
using Serilog;

namespace Ghuboon.Tests.Infrastructure.GitHub;

public class GitHubApiClientTests
{
    private const string Pat = "ghp_" + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef0123";

    private static (GitHubApiClient client, TestHttpMessageHandler handler, TestLogSink sink)
        BuildClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new TestHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri(GitHubApiClient.DefaultBaseUrl) };
        var sink = new TestLogSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.With(new RedactionEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();
        var client = new GitHubApiClient(http, logger);
        return (client, handler, sink);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json, params (string Name, string Value)[] headers)
    {
        var msg = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            msg.Headers.TryAddWithoutValidation(name, value);
        }
        return msg;
    }

    [Fact]
    public async Task ValidateAsync_returns_valid_on_200()
    {
        var (client, handler, _) = BuildClient(req => Json(HttpStatusCode.OK, "{\"login\":\"octocat\"}"));

        var result = await client.ValidateAsync(Pat);

        Assert.True(result.IsValid);
        Assert.Equal("octocat", result.Login);
        Assert.Null(result.Error);

        // Verify expected headers were set on the request.
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("Ghuboon/0.1", sent.Headers.UserAgent.ToString());
        Assert.Contains(sent.Headers.Accept, h => h.MediaType == "application/vnd.github+json");
        Assert.True(sent.Headers.Contains(GitHubApiClient.ApiVersionHeader));
        Assert.NotNull(sent.Headers.Authorization);
        Assert.Equal("token", sent.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task ValidateAsync_returns_auth_on_401()
    {
        var (client, _, _) = BuildClient(req => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"message\":\"Bad credentials\"}", Encoding.UTF8, "application/json"),
        });

        var result = await client.ValidateAsync(Pat);

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCategory.Auth, result.Error);
        Assert.Null(result.Login);
    }

    [Fact]
    public async Task ListNotificationsAsync_200_deserializes_and_captures_etag_and_rate_limit()
    {
        const string body = """
        [
          {
            "id": "1234",
            "repository": {
              "full_name": "octo/hello",
              "name": "hello",
              "html_url": "https://github.com/octo/hello",
              "owner": {"login": "octo"}
            },
            "subject": {
              "title": "A PR",
              "type": "PullRequest",
              "url": "https://api.github.com/repos/octo/hello/pulls/42"
            },
            "reason": "review_requested",
            "unread": true,
            "updated_at": "2025-01-02T03:04:05Z",
            "last_read_at": null
          }
        ]
        """;

        var (client, handler, _) = BuildClient(req => Json(
            HttpStatusCode.OK,
            body,
            ("ETag", "\"abc123\""),
            ("X-RateLimit-Remaining", "4321"),
            ("X-RateLimit-Reset", "1735689600")));

        var resp = await client.ListNotificationsAsync(Pat, new NotificationsRequest(AccountId: "acct-1"));

        Assert.False(resp.NotModified);
        Assert.Equal("\"abc123\"", resp.Etag);
        Assert.Equal(4321, resp.RateLimit.Remaining);
        Assert.NotNull(resp.RateLimit.ResetAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1735689600), resp.RateLimit.ResetAt);

        var n = Assert.Single(resp.Notifications);
        Assert.Equal("acct-1:1234", n.Id);
        Assert.Equal("1234", n.ThreadId);
        Assert.Equal("octo/hello", n.RepositoryFullName);
        Assert.Equal("PullRequest", n.Subject.Type);
        Assert.Equal("https://github.com/octo/hello/pull/42", n.Subject.WebUrl);
        Assert.True(n.Unread);

        // Sanity: no If-None-Match was set when not provided.
        var sent = Assert.Single(handler.Requests);
        Assert.False(sent.Headers.IfNoneMatch.Any());
    }

    [Fact]
    public async Task ListNotificationsAsync_304_returns_not_modified()
    {
        var (client, _, _) = BuildClient(req =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.NotModified);
            msg.Headers.TryAddWithoutValidation("ETag", "\"abc123\"");
            return msg;
        });

        var resp = await client.ListNotificationsAsync(Pat, new NotificationsRequest(IfNoneMatch: "\"abc123\""));

        Assert.True(resp.NotModified);
        Assert.Empty(resp.Notifications);
        Assert.Equal("\"abc123\"", resp.Etag);
    }

    [Fact]
    public async Task ListNotificationsAsync_sends_if_none_match_when_etag_supplied()
    {
        var (client, handler, _) = BuildClient(req => new HttpResponseMessage(HttpStatusCode.NotModified));

        await client.ListNotificationsAsync(Pat, new NotificationsRequest(IfNoneMatch: "\"prev-etag\""));

        var sent = Assert.Single(handler.Requests);
        Assert.True(sent.Headers.IfNoneMatch.Any());
        Assert.Equal("\"prev-etag\"", sent.Headers.IfNoneMatch.First().Tag);
    }

    [Fact]
    public async Task ListNotificationsAsync_serializes_query_parameters()
    {
        Uri? capturedUri = null;
        var (client, _, _) = BuildClient(req =>
        {
            capturedUri = req.RequestUri;
            return Json(HttpStatusCode.OK, "[]");
        });

        var since = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await client.ListNotificationsAsync(Pat, new NotificationsRequest(All: true, Participating: true, Since: since));

        Assert.NotNull(capturedUri);
        var query = capturedUri!.Query;
        Assert.Contains("all=true", query, StringComparison.Ordinal);
        Assert.Contains("participating=true", query, StringComparison.Ordinal);
        Assert.Contains("since=2025-01-02T03%3a04%3a05Z", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListNotificationsAsync_403_with_zero_remaining_maps_to_rate_limit()
    {
        var (client, _, _) = BuildClient(req => Json(
            HttpStatusCode.Forbidden,
            "{\"message\":\"API rate limit exceeded\"}",
            ("X-RateLimit-Remaining", "0"),
            ("X-RateLimit-Reset", "1735689600")));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.ListNotificationsAsync(Pat, new NotificationsRequest()));

        Assert.Equal(ErrorCategory.RateLimit, ex.Category);
        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task ListNotificationsAsync_403_with_saml_sso_body_maps_to_auth()
    {
        var (client, _, _) = BuildClient(req => Json(
            HttpStatusCode.Forbidden,
            "{\"message\":\"You must have admin access. Resource protected by organization SAML enforcement. You must grant your personal token access to this organization.\",\"documentation_url\":\"https://docs.github.com/articles/authenticating-with-saml-single-sign-on\"}",
            ("X-RateLimit-Remaining", "4999")));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.ListNotificationsAsync(Pat, new NotificationsRequest()));

        Assert.Equal(ErrorCategory.Auth, ex.Category);
        Assert.Contains("SAML SSO", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkThreadReadAsync_205_succeeds()
    {
        var (client, handler, _) = BuildClient(req => new HttpResponseMessage(HttpStatusCode.ResetContent));

        await client.MarkThreadReadAsync(Pat, "9999");

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.NotNull(sent.RequestUri);
        Assert.EndsWith("notifications/threads/9999", sent.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkThreadReadAsync_401_throws_auth()
    {
        var (client, _, _) = BuildClient(req => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.MarkThreadReadAsync(Pat, "1"));

        Assert.Equal(ErrorCategory.Auth, ex.Category);
    }

    [Fact]
    public async Task Authorization_header_never_appears_in_log_output()
    {
        var (client, _, sink) = BuildClient(req =>
        {
            // Echo request headers in body to be extra adversarial — our code should still not
            // log Authorization. We never log raw bodies in production paths.
            var auth = req.Headers.Authorization?.Parameter ?? "";
            return Json(HttpStatusCode.OK, "{\"login\":\"octocat\",\"echo\":\"" + auth + "\"}");
        });

        await client.ValidateAsync(Pat);

        // Make sure logging happened.
        Assert.NotEmpty(sink.Events);
        foreach (var line in sink.Events)
        {
            Assert.DoesNotContain(Pat, line, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization: token", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ghp_", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ListNotificationsAsync_500_maps_to_network()
    {
        var (client, _, _) = BuildClient(req => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream blew up"),
        });

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.ListNotificationsAsync(Pat, new NotificationsRequest()));

        Assert.Equal(ErrorCategory.Network, ex.Category);
    }

    [Fact]
    public async Task ListNotificationsAsync_422_maps_to_api_compatibility()
    {
        var (client, _, _) = BuildClient(req => Json(HttpStatusCode.UnprocessableEntity, "{}"));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.ListNotificationsAsync(Pat, new NotificationsRequest()));

        Assert.Equal(ErrorCategory.ApiCompatibility, ex.Category);
    }

    [Fact]
    public async Task ValidateAsync_network_failure_maps_to_network()
    {
        var handler = new TestHttpMessageHandler((req, ct) => throw new HttpRequestException("dns failure"));
        var http = new HttpClient(handler) { BaseAddress = new Uri(GitHubApiClient.DefaultBaseUrl) };
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().CreateLogger();
        var client = new GitHubApiClient(http, logger);

        var result = await client.ValidateAsync(Pat);

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCategory.Network, result.Error);
    }
}
