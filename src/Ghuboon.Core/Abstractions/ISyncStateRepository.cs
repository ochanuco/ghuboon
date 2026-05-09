using Ghuboon.Core.Domain;

namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Persistence for per-account sync bookkeeping such as ETag and rate-limit info.
/// </summary>
public interface ISyncStateRepository
{
    Task<SyncState?> GetAsync(string accountId, CancellationToken ct = default);

    Task UpsertAsync(SyncState state, CancellationToken ct = default);
}
