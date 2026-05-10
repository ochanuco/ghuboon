using System.Collections.Concurrent;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Settings;

internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_entries.TryGetValue(key, out var v) ? v : null);

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

    public bool Contains(string key) => _entries.ContainsKey(key);
    public string? Peek(string key) => _entries.TryGetValue(key, out var v) ? v : null;
}

internal sealed class FakeAppSettingsRepository : IAppSettingsRepository
{
    public ConcurrentDictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }
}

internal sealed class FakeAccountRepository : IAccountRepository
{
    public ConcurrentDictionary<string, Account> Accounts { get; } = new(StringComparer.Ordinal);

    public Task UpsertAsync(Account account, CancellationToken ct = default)
    {
        Accounts[account.Id] = account;
        return Task.CompletedTask;
    }

    public Task<Account?> GetByIdAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Accounts.TryGetValue(id, out var v) ? v : null);

    public Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Account>>(Accounts.Values.ToList());
}

internal sealed class FakeNotificationRepository : INotificationRepository
{
    public List<DateTimeOffset> DeleteOlderThanCalls { get; } = new();
    public int RowsToReturn { get; set; } = 5;

    public Task UpsertAsync(GitHubNotification notification, string rawJson, DateTimeOffset syncedAt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<GitHubNotification?> GetByIdAsync(string id, CancellationToken ct = default)
        => Task.FromResult<GitHubNotification?>(null);

    public Task<IReadOnlyList<GitHubNotification>> ListByAccountAsync(string accountId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GitHubNotification>>(Array.Empty<GitHubNotification>());

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        DeleteOlderThanCalls.Add(cutoff);
        return Task.FromResult(RowsToReturn);
    }
}

internal sealed class FakeGitHubApiClient : IGitHubApiClient
{
    public Func<string, UserValidationResult>? ValidateImpl { get; set; }
    public List<string> ValidateInvocations { get; } = new();

    public Task<UserValidationResult> ValidateAsync(string pat, CancellationToken ct = default)
    {
        ValidateInvocations.Add(pat);
        var impl = ValidateImpl ?? (_ => new UserValidationResult(true, "octocat", null, null));
        return Task.FromResult(impl(pat));
    }

    public Task<NotificationsResponse> ListNotificationsAsync(string pat, NotificationsRequest request, CancellationToken ct = default)
        => Task.FromResult(new NotificationsResponse(Array.Empty<GitHubNotification>(), null, RateLimitInfo.Empty, false));

    public Task MarkThreadReadAsync(string pat, string threadId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string?> GetSubjectBodyAsync(string pat, string subjectApiUrl, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<string?> GetThreadSubjectUrlAsync(string pat, string threadId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
