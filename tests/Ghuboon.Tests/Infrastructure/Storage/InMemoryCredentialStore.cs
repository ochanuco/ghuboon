using System.Collections.Concurrent;
using Ghuboon.Core.Abstractions;

namespace Ghuboon.Tests.Infrastructure.Storage;

/// <summary>
/// Test-only in-memory <see cref="ICredentialStore"/>. NEVER used in production
/// builds; storage tests use it to avoid hitting the real OS credential store.
/// </summary>
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(_entries.TryGetValue(key, out var value) ? value : null);
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

    public bool Contains(string key) => _entries.ContainsKey(key);
}
