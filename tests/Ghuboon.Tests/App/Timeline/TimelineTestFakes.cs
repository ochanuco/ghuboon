using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Timeline;

/// <summary>
/// In-memory <see cref="INotificationRepository"/> for the App-layer tests.
/// Mirrors the Sync tests' fixture but lives here so the App tests don't have
/// to depend on internal Sync types.
/// </summary>
internal sealed class FakeNotificationRepository : INotificationRepository
{
    public List<GitHubNotification> Notifications { get; } = new();
    public int UpsertCallCount { get; private set; }

    public Task UpsertAsync(GitHubNotification notification, string rawJson, DateTimeOffset syncedAt, CancellationToken ct = default)
    {
        UpsertCallCount++;
        var idx = Notifications.FindIndex(n => n.Id == notification.Id);
        if (idx >= 0)
        {
            Notifications[idx] = notification;
        }
        else
        {
            Notifications.Add(notification);
        }
        return Task.CompletedTask;
    }

    public Task<GitHubNotification?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        return Task.FromResult(Notifications.Find(n => n.Id == id));
    }

    public Task<IReadOnlyList<GitHubNotification>> ListByAccountAsync(string accountId, CancellationToken ct = default)
    {
        IReadOnlyList<GitHubNotification> list = Notifications
            .FindAll(n => n.AccountId == accountId)
            .ConvertAll(n => n);
        return Task.FromResult(list);
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        return Task.FromResult(0);
    }

    public Task<int> SetActorLoginAsync(string id, string actorLogin, CancellationToken ct = default)
    {
        var idx = Notifications.FindIndex(n => n.Id == id);
        if (idx < 0) return Task.FromResult(0);
        Notifications[idx] = Notifications[idx] with { ActorLogin = actorLogin };
        return Task.FromResult(1);
    }

    public int SetReadStateCallCount { get; private set; }

    public Task<int> SetReadStateAsync(string id, bool unread, DateTimeOffset readAt, CancellationToken ct = default)
    {
        SetReadStateCallCount++;
        var idx = Notifications.FindIndex(n => n.Id == id);
        if (idx < 0) return Task.FromResult(0);
        Notifications[idx] = Notifications[idx] with { Unread = unread, LastReadAt = readAt };
        return Task.FromResult(1);
    }
}

/// <summary>
/// In-memory <see cref="INotificationEventRepository"/> for the App-layer tests.
/// Mirrors the production semantics: <see cref="TryAppendAsync"/> dedups on
/// (account_id, notification_id, source_updated_at) and
/// <see cref="MarkThreadAsReadAsync"/> flips every sibling event row.
/// </summary>
internal sealed class FakeNotificationEventRepository : INotificationEventRepository
{
    public List<NotificationEvent> Events { get; } = new();
    public int AppendCallCount { get; private set; }
    public int MarkReadCallCount { get; private set; }
    private long _nextId = 1;

    public Task<bool> TryAppendAsync(NotificationEvent ev, CancellationToken ct = default)
    {
        AppendCallCount++;
        if (Events.Any(e => e.AccountId == ev.AccountId
                            && e.NotificationId == ev.NotificationId
                            && e.SourceUpdatedAt == ev.SourceUpdatedAt))
        {
            return Task.FromResult(false);
        }

        var assigned = ev.Id > 0 ? ev : new NotificationEvent(
            Id: _nextId++,
            AccountId: ev.AccountId,
            NotificationId: ev.NotificationId,
            ThreadId: ev.ThreadId,
            RepositoryFullName: ev.RepositoryFullName,
            Subject: ev.Subject,
            Reason: ev.Reason,
            SourceUpdatedAt: ev.SourceUpdatedAt,
            ObservedAt: ev.ObservedAt,
            Unread: ev.Unread,
            LastReadAt: ev.LastReadAt,
            RawJson: ev.RawJson);
        Events.Add(assigned);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<NotificationEvent>> ListByAccountAsync(string accountId, int limit, CancellationToken ct = default)
    {
        // Tween-like timeline: cap to the latest N events by observed_at
        // (when WE saw them) but return them ordered by source_updated_at
        // (when GitHub last touched the thread) so the displayed order
        // matches the "Updated" column the UI surfaces.
        var newest = Events
            .Where(e => e.AccountId == accountId)
            .OrderByDescending(e => e.ObservedAt)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToList();
        IReadOnlyList<NotificationEvent> list = newest
            .OrderBy(e => e.SourceUpdatedAt)
            .ThenBy(e => e.Id)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<int> MarkThreadAsReadAsync(string accountId, string notificationId, DateTimeOffset readAt, CancellationToken ct = default)
    {
        MarkReadCallCount++;
        var affected = 0;
        for (var i = 0; i < Events.Count; i++)
        {
            var e = Events[i];
            if (e.AccountId == accountId && e.NotificationId == notificationId)
            {
                Events[i] = e with { Unread = false, LastReadAt = readAt };
                affected++;
            }
        }
        return Task.FromResult(affected);
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var removed = Events.RemoveAll(e => e.ObservedAt < cutoff);
        return Task.FromResult(removed);
    }

    public Task<DateTimeOffset?> GetMaxSourceUpdatedAtForThreadAsync(string accountId, string notificationId, CancellationToken ct = default)
    {
        var max = Events
            .Where(e => e.AccountId == accountId && e.NotificationId == notificationId)
            .Select(e => (DateTimeOffset?)e.SourceUpdatedAt)
            .DefaultIfEmpty()
            .Max();
        return Task.FromResult(max);
    }

    public Task<int> SetActorLoginAsync(long eventId, string actorLogin, CancellationToken ct = default)
    {
        for (var i = 0; i < Events.Count; i++)
        {
            if (Events[i].Id == eventId)
            {
                Events[i] = Events[i] with { ActorLogin = actorLogin };
                return Task.FromResult(1);
            }
        }
        return Task.FromResult(0);
    }

    public Task<int> SetBodyAsync(long eventId, string? body, string? bodyAuthorLogin, CancellationToken ct = default)
    {
        for (var i = 0; i < Events.Count; i++)
        {
            if (Events[i].Id == eventId)
            {
                Events[i] = Events[i] with { Body = body, BodyAuthorLogin = bodyAuthorLogin };
                return Task.FromResult(1);
            }
        }
        return Task.FromResult(0);
    }
}

internal sealed class FakeRepositoryRepository : IRepositoryRepository
{
    public List<RepositoryRef> Repositories { get; } = new();

    public Task UpsertAsync(RepositoryRef repository, CancellationToken ct = default)
    {
        Repositories.Add(repository);
        return Task.CompletedTask;
    }

    public Task<RepositoryRef?> GetByFullNameAsync(string accountId, string fullName, CancellationToken ct = default)
    {
        return Task.FromResult(Repositories.Find(r => r.AccountId == accountId && r.FullName == fullName));
    }

    public Task<IReadOnlyList<RepositoryRef>> ListByAccountAsync(string accountId, CancellationToken ct = default)
    {
        IReadOnlyList<RepositoryRef> list = Repositories
            .FindAll(r => r.AccountId == accountId)
            .ConvertAll(r => r);
        return Task.FromResult(list);
    }
}

internal sealed class FakeAccountRepository : IAccountRepository
{
    public List<Account> Accounts { get; } = new();

    public Task UpsertAsync(Account account, CancellationToken ct = default)
    {
        var idx = Accounts.FindIndex(a => a.Id == account.Id);
        if (idx >= 0) Accounts[idx] = account;
        else Accounts.Add(account);
        return Task.CompletedTask;
    }

    public Task<Account?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        return Task.FromResult(Accounts.Find(a => a.Id == id));
    }

    public Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Account> list = Accounts.ConvertAll(a => a);
        return Task.FromResult(list);
    }
}

internal sealed class FakeApiClient : IGitHubApiClient
{
    // Issue #27: concurrent MarkThreadReadAsync invocations may race the list,
    // so use a ConcurrentBag to record threadIds without locking. Tests that
    // care about ordering should call .ToList() to take a snapshot.
    public ConcurrentBag<string> MarkedReadThreads { get; } = new();
    public Func<string, Exception?>? MarkReadOverride { get; set; }
    /// <summary>
    /// Optional gate; when set, <see cref="MarkThreadReadAsync"/> awaits this task
    /// before completing. Lets tests assert concurrency / coalescing behavior.
    /// </summary>
    public TaskCompletionSource? MarkReadGate { get; set; }

    public Task<UserValidationResult> ValidateAsync(string pat, CancellationToken ct = default)
        => Task.FromResult(new UserValidationResult(true, "octocat", null, null));

    public Task<NotificationsResponse> ListNotificationsAsync(string pat, NotificationsRequest request, CancellationToken ct = default)
        => Task.FromResult(new NotificationsResponse(Array.Empty<GitHubNotification>(), null, RateLimitInfo.Empty, false));

    public async Task MarkThreadReadAsync(string pat, string threadId, CancellationToken ct = default)
    {
        if (MarkReadGate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }
        if (MarkReadOverride is { } o)
        {
            var ex = o(threadId);
            if (ex is not null) throw ex;
        }
        MarkedReadThreads.Add(threadId);
    }

    public Func<string, string?>? GetSubjectBodyOverride { get; set; }

    public Task<string?> GetSubjectBodyAsync(string pat, string subjectApiUrl, CancellationToken ct = default)
        => Task.FromResult(GetSubjectBodyOverride?.Invoke(subjectApiUrl));

    public Func<string, string?>? GetThreadSubjectUrlOverride { get; set; }

    public Task<string?> GetThreadSubjectUrlAsync(string pat, string threadId, CancellationToken ct = default)
        => Task.FromResult(GetThreadSubjectUrlOverride?.Invoke(threadId));

    public Func<string, string?>? GetLatestCommentBodyOverride { get; set; }

    public Task<string?> GetLatestCommentBodyAsync(string pat, string threadId, CancellationToken ct = default)
        => Task.FromResult(GetLatestCommentBodyOverride?.Invoke(threadId));

    public Func<string, (string?, string?)>? GetLatestCommentDetailsOverride { get; set; }

    public Task<(string? Body, string? AuthorLogin)> GetLatestCommentDetailsAsync(string pat, string threadId, CancellationToken ct = default)
        => Task.FromResult(GetLatestCommentDetailsOverride?.Invoke(threadId) ?? (null, null));

    public Func<string, (string?, string?)>? GetSubjectBodyAndAuthorOverride { get; set; }

    public Task<(string? Body, string? AuthorLogin)> GetSubjectBodyAndAuthorAsync(string pat, string subjectApiUrl, CancellationToken ct = default)
        => Task.FromResult(GetSubjectBodyAndAuthorOverride?.Invoke(subjectApiUrl) ?? (null, null));
}

internal sealed class FakeBrowser : IBrowserService
{
    public List<string> OpenedUrls { get; } = new();
    public void OpenUrl(string url) => OpenedUrls.Add(url);
}

internal sealed class FakeClipboard : IClipboardService
{
    public List<string> Texts { get; } = new();
    public Task SetTextAsync(string text)
    {
        Texts.Add(text);
        return Task.CompletedTask;
    }
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan d) => UtcNow += d;
}

internal sealed class FakeSyncService : INotificationSyncService
{
    public bool IsRunning { get; private set; }
    public List<string> SyncCalls { get; } = new();
    public Func<string, SyncResult>? Result { get; set; }

    public event EventHandler<SyncProgressEvent>? Progress;
    public event EventHandler<NewNotificationsEvent>? NewNotifications;

    public Task<SyncResult> SyncAsync(string accountId, CancellationToken ct = default)
    {
        SyncCalls.Add(accountId);
        Progress?.Invoke(this, new SyncProgressEvent(accountId, SyncStage.Starting, null));
        var r = Result?.Invoke(accountId)
                ?? new SyncResult(true, 0, 0, 0, null, null, RateLimitInfo.Empty);
        var stage = r.Success ? SyncStage.Completed : SyncStage.Failed;
        Progress?.Invoke(this, new SyncProgressEvent(accountId, stage, r));
        return Task.FromResult(r);
    }

    public void RaiseProgress(SyncProgressEvent ev) => Progress?.Invoke(this, ev);

    public void RaiseNewNotifications(NewNotificationsEvent ev) => NewNotifications?.Invoke(this, ev);

    public void Start() => IsRunning = true;
    public void Stop() => IsRunning = false;
}

internal static class TimelineTestData
{
    public static GitHubNotification Build(
        string id,
        string accountId,
        string repo,
        string title,
        NotificationReason reason,
        bool unread,
        DateTimeOffset updatedAt,
        string subjectType = "PullRequest",
        string? webUrl = null,
        string? threadId = null)
    {
        // GitHubNotification invariants require Id == "{AccountId}:{ThreadId}".
        // When threadId is omitted, derive it from the part of `id` after the
        // first ':' (so historical inputs like ("primary:a", "primary") keep
        // producing Id="primary:a", ThreadId="a").
        if (threadId is null)
        {
            var sep = id.IndexOf(':');
            threadId = sep >= 0 ? id[(sep + 1)..] : id;
        }

        return new GitHubNotification(
            id,
            accountId,
            threadId,
            repo,
            new NotificationSubject(subjectType, title, null, webUrl),
            reason,
            unread,
            updatedAt,
            null);
    }

    /// <summary>
    /// Test-side <see cref="NotificationEvent"/> factory mirroring
    /// <see cref="Build(string, string, string, string, NotificationReason, bool, DateTimeOffset, string, string?, string?)"/>'s
    /// signature so call sites can swap from the GitHubNotification fixture to
    /// the event fixture without rewriting the call.
    /// </summary>
    public static NotificationEvent BuildEvent(
        long eventId,
        string id,
        string accountId,
        string repo,
        string title,
        NotificationReason reason,
        bool unread,
        DateTimeOffset updatedAt,
        string subjectType = "PullRequest",
        string? webUrl = null,
        string? threadId = null,
        DateTimeOffset? observedAt = null)
    {
        if (threadId is null)
        {
            var sep = id.IndexOf(':');
            threadId = sep >= 0 ? id[(sep + 1)..] : id;
        }

        return new NotificationEvent(
            Id: eventId,
            AccountId: accountId,
            NotificationId: id,
            ThreadId: threadId,
            RepositoryFullName: repo,
            Subject: new NotificationSubject(subjectType, title, null, webUrl),
            Reason: reason,
            SourceUpdatedAt: updatedAt,
            ObservedAt: observedAt ?? updatedAt,
            Unread: unread,
            LastReadAt: null,
            RawJson: "{}");
    }
}
