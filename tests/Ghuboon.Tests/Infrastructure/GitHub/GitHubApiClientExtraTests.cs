using System;
using System.Net;
using System.Net.Http;
using System.Text;
using Ghuboon.Core.Abstractions;
using Ghuboon.Infrastructure.GitHub;
using Serilog;

namespace Ghuboon.Tests.Infrastructure.GitHub;

/// <summary>
/// Phase 15: targeted gap-fill tests for <see cref="GitHubApiClient"/>.
/// Existing tests cover most paths; this fixture adds edge-case coverage.
/// </summary>
public class GitHubApiClientExtraTests
{
    private const string Pat = "ghp_" + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef0123";

    private static (GitHubApiClient client, TestHttpMessageHandler handler)
        Build(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new TestHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri(GitHubApiClient.DefaultBaseUrl) };
        var logger = new LoggerConfiguration().CreateLogger();
        return (new GitHubApiClient(http, logger), handler);
    }

    [Fact]
    public async Task ListNotificationsAsync_200_WithEmptyArray_StillCapturesEtagAndRateLimit()
    {
        var (client, _) = Build(_ =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            };
            msg.Headers.TryAddWithoutValidation("ETag", "\"empty-etag\"");
            msg.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "4998");
            return msg;
        });

        var resp = await client.ListNotificationsAsync(Pat, new NotificationsRequest());

        Assert.False(resp.NotModified);
        Assert.Empty(resp.Notifications);
        Assert.Equal("\"empty-etag\"", resp.Etag);
        Assert.Equal(4998, resp.RateLimit.Remaining);
    }

    [Fact]
    public async Task ListNotificationsAsync_MalformedJson_Throws()
    {
        // Current behavior: an unparseable JSON body bubbles up as a JsonException
        // (wrapped or raw) rather than being mapped into a GitHubApiException with
        // ApiCompatibility category. Documented as a gap; see notes.
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ this is not valid JSON ]", Encoding.UTF8, "application/json"),
        });

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.ListNotificationsAsync(Pat, new NotificationsRequest()));
    }

    [Fact]
    public async Task BuildRequest_AuthorizationHeader_UsesTokenScheme_NotBearer()
    {
        var (client, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });

        await client.ListNotificationsAsync(Pat, new NotificationsRequest());

        var sent = Assert.Single(handler.Requests);
        Assert.NotNull(sent.Headers.Authorization);
        Assert.Equal("token", sent.Headers.Authorization!.Scheme);
        Assert.NotEqual("Bearer", sent.Headers.Authorization.Scheme);
        Assert.Equal(Pat, sent.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ListNotificationsAsync_NoIfNoneMatch_NotSent_WhenCallerOmits()
    {
        var (client, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });

        // Empty IfNoneMatch -> header not added.
        await client.ListNotificationsAsync(Pat, new NotificationsRequest(IfNoneMatch: null));
        await client.ListNotificationsAsync(Pat, new NotificationsRequest(IfNoneMatch: ""));

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, req => Assert.False(req.Headers.IfNoneMatch.Any()));
    }

    [Fact]
    public async Task ListNotificationsAsync_Since_SerializedAsIso8601Utc()
    {
        Uri? captured = null;
        var (client, _) = Build(req =>
        {
            captured = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            };
        });

        // Non-UTC offset; the API client must convert to UTC before serializing.
        var since = new DateTimeOffset(2026, 5, 9, 14, 30, 45, TimeSpan.FromHours(9));
        await client.ListNotificationsAsync(Pat, new NotificationsRequest(Since: since));

        Assert.NotNull(captured);
        // 14:30:45+09:00 -> 05:30:45Z
        Assert.Contains("since=2026-05-09T05%3a30%3a45Z", captured!.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkThreadReadAsync_NetworkException_PropagatesAsHttpRequestException()
    {
        // Phase 15 finding: MarkThreadReadAsync does NOT translate HttpRequestException
        // into GitHubApiException(ErrorCategory.Network) the way ValidateAsync /
        // ListNotificationsAsync do. We pin down current behavior here so a future
        // fix for issue #11 has a regression target.
        var handler = new TestHttpMessageHandler((req, ct) => throw new HttpRequestException("dns failure"));
        var http = new HttpClient(handler) { BaseAddress = new Uri(GitHubApiClient.DefaultBaseUrl) };
        var client = new GitHubApiClient(http, new LoggerConfiguration().CreateLogger());

        // TODO(issue-11): once mark-read failures are mapped to GitHubApiException,
        // tighten this assertion to expect the mapped exception + ErrorCategory.Network.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.MarkThreadReadAsync(Pat, "1234"));
    }
}
