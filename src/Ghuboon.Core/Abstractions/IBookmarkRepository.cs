namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Per-thread bookmark flag persisted in <c>notification_local_states</c>.
/// Survives sync / cache prune (LocalState rows persist independently of
/// notifications and events). Used by the Bookmarks tab and the
/// Shift+S / Shift+Ctrl+S keyboard shortcuts.
/// </summary>
public interface IBookmarkRepository
{
    /// <summary>
    /// Mark a notification as bookmarked. Idempotent — re-bookmarking an
    /// already-bookmarked thread is a no-op except for refreshing
    /// <paramref name="at"/>.
    /// </summary>
    Task SetAsync(string accountId, string notificationId, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Clear the bookmark on a thread. Idempotent.</summary>
    Task ClearAsync(string accountId, string notificationId, CancellationToken ct = default);

    /// <summary>
    /// Return the set of bookmarked notification IDs for the given account.
    /// The view layer dedups this set against the in-memory timeline cache,
    /// so a one-shot query per Reload is cheap enough.
    /// </summary>
    Task<IReadOnlySet<string>> GetBookmarkedIdsAsync(string accountId, CancellationToken ct = default);
}
