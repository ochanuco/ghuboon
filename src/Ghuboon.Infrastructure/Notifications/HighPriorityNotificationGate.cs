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
    // OS-banner reasons. The PLAN.md baseline keeps to the high-signal four
    // (Review/Mention/TeamMention/Assigned). User-driven additions: MyPr and
    // State surface authored-PR activity and Draft ⇄ Open toggles; Comment
    // surfaces every PR/Issue comment so the user can read replies without
    // tabbing back. Subscribed / Watching / CiActivity remain suppressed
    // because they are noisy (release bots, dependabot, periodic CI runs).
    private static readonly IReadOnlySet<NotificationReason> HighPriorityReasons =
        new HashSet<NotificationReason>
        {
            NotificationReason.Review,
            NotificationReason.Mention,
            NotificationReason.TeamMention,
            NotificationReason.Assigned,
            NotificationReason.MyPr,
            NotificationReason.State,
            NotificationReason.Comment,
        };

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
        var now = _clock.UtcNow;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!HighPriorityReasons.Contains(candidate.Reason))
            {
                continue;
            }

            // Atomic mark-and-claim eliminates the TOCTOU window between a
            // separate Get and Set: under concurrent sync the SQL statement
            // either marks the row (returns true) or observes another writer
            // already marked it (returns false). Only the winner emits the
            // notification, preventing duplicates.
            if (await _tracker
                    .TryMarkAsNotifiedAsync(accountId, candidate.Id, now, ct)
                    .ConfigureAwait(false))
            {
                accepted.Add(candidate);
            }
        }

        return accepted;
    }
}
