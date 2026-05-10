namespace Ghuboon.Core.Domain;

/// <summary>
/// Per-account sync bookkeeping. Used by the sync service to drive conditional
/// requests and to surface rate-limit info to the UI.
/// </summary>
public sealed record SyncState(
    string AccountId,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset? LastSuccessfulSyncAt,
    string? NotificationsEtag,
    int? RateLimitRemaining,
    DateTimeOffset? RateLimitResetAt
)
{
    /// <summary>
    /// Build an empty <see cref="SyncState"/> for the given account.
    /// </summary>
    /// <param name="accountId">Owning account id; must be non-null and not whitespace.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="accountId"/> is null, empty, or whitespace.
    /// </exception>
    public static SyncState Empty(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return new SyncState(accountId, null, null, null, null, null);
    }
}
