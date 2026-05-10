using Ghuboon.Core.Abstractions;
using Ghuboon.Infrastructure.GitHub;

namespace Ghuboon.Tests.Infrastructure.Sync;

/// <summary>
/// Test-only <see cref="IGitHubApiClient"/> driven by a queue of scripted responses
/// so tests can simulate first-sync, 304, errors, and rate-limit cases without
/// going through HTTP.
/// </summary>
internal sealed class FakeGitHubApiClient : IGitHubApiClient
{
    private readonly Queue<Func<NotificationsRequest, NotificationsResponse>> _listResponses = new();
    public List<NotificationsRequest> Requests { get; } = new();
    public Func<NotificationsRequest, Exception>? ListThrows { get; set; }
    public List<string> MarkedReadThreads { get; } = new();

    public void EnqueueList(NotificationsResponse response) =>
        _listResponses.Enqueue(_ => response);

    public void EnqueueList(Func<NotificationsRequest, NotificationsResponse> factory) =>
        _listResponses.Enqueue(factory);

    public Task<UserValidationResult> ValidateAsync(string pat, CancellationToken ct = default)
    {
        return Task.FromResult(new UserValidationResult(true, "octocat", null, null));
    }

    public Task<NotificationsResponse> ListNotificationsAsync(string pat, NotificationsRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);

        if (ListThrows is not null)
        {
            throw ListThrows(request);
        }

        if (_listResponses.Count == 0)
        {
            // Default to NotModified to keep periodic loops harmless.
            return Task.FromResult(new NotificationsResponse(
                Array.Empty<Ghuboon.Core.Domain.GitHubNotification>(),
                request.IfNoneMatch,
                RateLimitInfo.Empty,
                NotModified: true));
        }

        var factory = _listResponses.Dequeue();
        return Task.FromResult(factory(request));
    }

    public Task MarkThreadReadAsync(string pat, string threadId, CancellationToken ct = default)
    {
        MarkedReadThreads.Add(threadId);
        return Task.CompletedTask;
    }

    public Task<string?> GetSubjectBodyAsync(string pat, string subjectApiUrl, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<string?> GetThreadSubjectUrlAsync(string pat, string threadId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<string?> GetLatestCommentBodyAsync(string pat, string threadId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<(string? Body, string? AuthorLogin)> GetLatestCommentDetailsAsync(string pat, string threadId, CancellationToken ct = default)
        => Task.FromResult<(string?, string?)>((null, null));

    public Task<(string? Body, string? AuthorLogin)> GetSubjectBodyAndAuthorAsync(string pat, string subjectApiUrl, CancellationToken ct = default)
        => Task.FromResult<(string?, string?)>((null, null));
}
