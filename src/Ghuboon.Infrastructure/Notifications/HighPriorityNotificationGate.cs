using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Infrastructure.Notifications;

/// <summary>
/// Default <see cref="IDesktopNotificationGate"/>. Applies the ADR-021 reason
/// allow-list and dedups via <c>notification_local_states.last_notified_at</c>.
/// Accepted items are recorded as notified before being returned, so a second
/// call with the same id is suppressed.
/// </summary>
public sealed class HighPriorityNotificationGate : IDesktopNotificationGate
{
    // OS-banner allow-list. Sourced from
    // Ghuboon.Core.Domain.HighPriorityNotificationReasons so the gate's
    // downstream filter and NotificationSyncService's upstream filter
    // share a single definition. They drifted once and silenced
    // MyPr / State / Comment banners for a release.

    private readonly LastNotifiedTracker _tracker;
    private readonly IClock _clock;

    public HighPriorityNotificationGate(IDbConnectionFactory connectionFactory, IClock clock)
        : this(new LastNotifiedTracker(connectionFactory), clock)
    {
    }

    internal HighPriorityNotificationGate(LastNotifiedTracker tracker, IClock clock)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<IReadOnlyList<GitHubNotification>> FilterAsync(
        string accountId,
        IReadOnlyList<GitHubNotification> candidates,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return Array.Empty<GitHubNotification>();
        }

        var accepted = new List<GitHubNotification>(candidates.Count);

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!HighPriorityNotificationReasons.Contains(candidate.Reason))
            {
                continue;
            }

            // Pass the candidate's UpdatedAt as the dedup axis: an event
            // with a newer source_updated_at than the prior banner wins
            // (and gets its banner), an idempotent re-observation at the
            // same timestamp loses (suppressed). The previous version
            // passed `now` and gated only on IS NULL, which silenced
            // every subsequent event on a thread that ever fired once.
            if (await _tracker
                    .TryMarkAsNotifiedAsync(accountId, candidate.Id, candidate.UpdatedAt, ct)
                    .ConfigureAwait(false))
            {
                accepted.Add(candidate);
            }
        }

        return accepted;
    }
}
