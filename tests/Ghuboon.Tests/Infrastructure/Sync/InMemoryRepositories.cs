using System.Collections.Concurrent;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.Infrastructure.Sync;

/// <summary>
/// Test-only in-memory <see cref="IAccountRepository"/>.
/// </summary>
internal sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    public int FailListCount { get; set; }

    public Task UpsertAsync(Account account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        _accounts[account.Id] = account;
        return Task.CompletedTask;
    }

    public Task<Account?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        return Task.FromResult(_accounts.TryGetValue(id, out var acc) ? acc : null);
    }

    public Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default)
    {
        if (FailListCount > 0)
        {
            FailListCount--;
            throw new InvalidOperationException("simulated list failure");
        }
        return Task.FromResult<IReadOnlyList<Account>>(_accounts.Values.OrderBy(a => a.CreatedAt).ToList());
    }
}

/// <summary>
/// Test-only in-memory <see cref="IRepositoryRepository"/>.
/// </summary>
internal sealed class InMemoryRepositoryRepository : IRepositoryRepository
{
    private readonly ConcurrentDictionary<string, RepositoryRef> _repos = new(StringComparer.Ordinal);
    public int UpsertCallCount { get; private set; }

    public Task UpsertAsync(RepositoryRef repository, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        UpsertCallCount++;
        _repos[$"{repository.AccountId}|{repository.FullName}"] = repository;
        return Task.CompletedTask;
    }

    public Task<RepositoryRef?> GetByFullNameAsync(string accountId, string fullName, CancellationToken ct = default)
    {
        return Task.FromResult(_repos.TryGetValue($"{accountId}|{fullName}", out var r) ? r : null);
    }

    public Task<IReadOnlyList<RepositoryRef>> ListByAccountAsync(string accountId, CancellationToken ct = default)
    {
        IReadOnlyList<RepositoryRef> list = _repos.Values.Where(r => r.AccountId == accountId).ToList();
        return Task.FromResult(list);
    }
}

/// <summary>
/// Test-only in-memory <see cref="INotificationRepository"/>. Tracks raw_json and
/// synced_at via internal records so tests can assert on prune behavior.
/// </summary>
internal sealed class InMemoryNotificationRepository : INotificationRepository
{
    private sealed record Entry(GitHubNotification Notification, string RawJson, DateTimeOffset SyncedAt);

    private readonly ConcurrentDictionary<string, Entry> _notifs = new(StringComparer.Ordinal);
    public int UpsertCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }
    public Func<GitHubNotification, string, DateTimeOffset, Exception?>? UpsertOverride { get; set; }

    public Task UpsertAsync(GitHubNotification notification, string rawJson, DateTimeOffset syncedAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        UpsertCallCount++;

        if (UpsertOverride is not null)
        {
            var ex = UpsertOverride(notification, rawJson, syncedAt);
            if (ex is not null)
            {
                throw ex;
            }
        }

        _notifs[notification.Id] = new Entry(notification, rawJson, syncedAt);
        return Task.CompletedTask;
    }

    public Task<GitHubNotification?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        return Task.FromResult(_notifs.TryGetValue(id, out var entry) ? entry.Notification : null);
    }

    public Task<IReadOnlyList<GitHubNotification>> ListByAccountAsync(string accountId, CancellationToken ct = default)
    {
        IReadOnlyList<GitHubNotification> list = _notifs.Values
            .Where(e => e.Notification.AccountId == accountId)
            .OrderByDescending(e => e.Notification.UpdatedAt)
            .Select(e => e.Notification)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        DeleteCallCount++;
        var stale = _notifs
            .Where(kv => kv.Value.SyncedAt < cutoff)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale)
        {
            _notifs.TryRemove(key, out _);
        }
        return Task.FromResult(stale.Count);
    }

    public Task<int> SetActorLoginAsync(string id, string actorLogin, CancellationToken ct = default)
    {
        if (!_notifs.TryGetValue(id, out var entry))
        {
            return Task.FromResult(0);
        }
        var updated = entry.Notification with { ActorLogin = actorLogin };
        _notifs[id] = entry with { Notification = updated };
        return Task.FromResult(1);
    }

    public int Count => _notifs.Count;

    public void SeedWithSyncedAt(GitHubNotification notification, DateTimeOffset syncedAt)
    {
        _notifs[notification.Id] = new Entry(notification, "{}", syncedAt);
    }
}

/// <summary>
/// Test-only in-memory <see cref="INotificationEventRepository"/>. Mirrors the
/// Storage implementation: dedups on
/// (account_id, notification_id, source_updated_at) and orders newest-first
/// by (observed_at, id).
/// </summary>
internal sealed class InMemoryNotificationEventRepository : INotificationEventRepository
{
    private sealed record Entry(NotificationEvent Event);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private long _nextId = 1;
    public int AppendCallCount { get; private set; }
    public int DedupedCount { get; private set; }
    public int DeleteCallCount { get; private set; }
    public int MarkReadCallCount { get; private set; }

    public Task<bool> TryAppendAsync(NotificationEvent ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        lock (_gate)
        {
            AppendCallCount++;
            if (_entries.Any(e => e.Event.AccountId == ev.AccountId
                                  && e.Event.NotificationId == ev.NotificationId
                                  && e.Event.SourceUpdatedAt == ev.SourceUpdatedAt))
            {
                DedupedCount++;
                return Task.FromResult(false);
            }

            var id = _nextId++;
            var assigned = new NotificationEvent(
                Id: id,
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
            _entries.Add(new Entry(assigned));
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<NotificationEvent>> ListByAccountAsync(string accountId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<NotificationEvent> list = _entries
                .Where(e => e.Event.AccountId == accountId)
                .OrderByDescending(e => e.Event.ObservedAt)
                .ThenByDescending(e => e.Event.Id)
                .Take(limit)
                .Select(e => e.Event)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<int> MarkThreadAsReadAsync(string accountId, string notificationId, DateTimeOffset readAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            MarkReadCallCount++;
            var affected = 0;
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i].Event;
                if (e.AccountId == accountId && e.NotificationId == notificationId)
                {
                    _entries[i] = new Entry(e with { Unread = false, LastReadAt = readAt });
                    affected++;
                }
            }
            return Task.FromResult(affected);
        }
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        lock (_gate)
        {
            DeleteCallCount++;
            var removed = _entries.RemoveAll(e => e.Event.ObservedAt < cutoff);
            return Task.FromResult(removed);
        }
    }

    public Task<DateTimeOffset?> GetMaxSourceUpdatedAtForThreadAsync(string accountId, string notificationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var max = _entries
                .Where(e => e.Event.AccountId == accountId && e.Event.NotificationId == notificationId)
                .Select(e => (DateTimeOffset?)e.Event.SourceUpdatedAt)
                .DefaultIfEmpty()
                .Max();
            return Task.FromResult(max);
        }
    }

    public Task<int> SetActorLoginAsync(long eventId, string actorLogin, CancellationToken ct = default)
    {
        lock (_gate)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var ev = _entries[i].Event;
                if (ev.Id == eventId)
                {
                    _entries[i] = new Entry(ev with { ActorLogin = actorLogin });
                    return Task.FromResult(1);
                }
            }
            return Task.FromResult(0);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }
}

/// <summary>
/// Test-only in-memory <see cref="ISyncStateRepository"/>.
/// </summary>
internal sealed class InMemorySyncStateRepository : ISyncStateRepository
{
    private readonly ConcurrentDictionary<string, SyncState> _states = new(StringComparer.Ordinal);
    public int UpsertCallCount { get; private set; }

    public Task<SyncState?> GetAsync(string accountId, CancellationToken ct = default)
    {
        return Task.FromResult(_states.TryGetValue(accountId, out var s) ? s : null);
    }

    public Task UpsertAsync(SyncState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        UpsertCallCount++;
        _states[state.AccountId] = state;
        return Task.CompletedTask;
    }

    public SyncState? Peek(string accountId) => _states.TryGetValue(accountId, out var s) ? s : null;
}

/// <summary>
/// Test-only in-memory <see cref="ICredentialStore"/> for sync tests. Mirrors the
/// Storage tests' fixture.
/// </summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);
    public bool ThrowOnGet { get; set; }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        if (ThrowOnGet)
        {
            throw new InvalidOperationException("simulated credential failure");
        }
        return Task.FromResult(_entries.TryGetValue(key, out var v) ? v : null);
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _entries[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
