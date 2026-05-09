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
    public static SyncState Empty(string accountId) =>
        new(accountId, null, null, null, null, null);
}
