using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.Services;

/// <summary>
/// Production <see cref="ITimelineService"/> backed by the encrypted SQLite cache.
/// Loads the per-update event log for the configured primary account and applies
/// the <see cref="TimelineFilter"/> in memory: tab filter (Phase 9), repo
/// dropdown, and free-text search.
///
/// Event-log timeline: rows are <see cref="NotificationEvent"/> instances drawn
/// from <see cref="INotificationEventRepository"/>, so a thread that updates
/// multiple times produces multiple rows. The presentation layer maps each
/// event to a <see cref="TimelineItemViewModel"/>.
/// </summary>
public sealed class DbBackedTimelineService : ITimelineService
{
    /// <summary>
    /// Page size for <see cref="LoadAsync"/>. The event log is append-only, so
    /// without an upper bound the timeline grows indefinitely as syncs run.
    /// 200 rows comfortably covers the visible window while keeping the
    /// in-memory filter pipeline cheap.
    /// </summary>
    public const int DefaultLimit = 200;

    private readonly INotificationEventRepository _events;
    private readonly IRepositoryRepository _repositories;
    private readonly IAccountRepository _accounts;
    private readonly string _accountId;
    private readonly int _limit;

    /// <summary>
    /// Optional factory for the per-row VM context. Retained so existing callers
    /// (App composition root, tests) can continue to wire one through; the
    /// <see cref="TimelineViewModel"/> reads it via <see cref="ItemContextFactory"/>
    /// to attach commands to the rows it builds.
    /// </summary>
    public Func<TimelineItemContext> ItemContextFactory { get; }

    public DbBackedTimelineService(
        INotificationEventRepository events,
        IRepositoryRepository repositories,
        IAccountRepository accounts,
        Func<TimelineItemContext>? itemContextFactory = null,
        string accountId = AppSettingsService.PrimaryAccountId,
        int limit = DefaultLimit)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        ItemContextFactory = itemContextFactory ?? (() => TimelineItemContext.Empty);
        _accountId = accountId;
        _limit = limit;
    }

    public async Task<IReadOnlyList<NotificationEvent>> LoadAsync(TimelineFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var rows = await _events.ListByAccountAsync(_accountId, _limit, ct).ConfigureAwait(false);

        IEnumerable<NotificationEvent> q = rows;

        // Tab filter.
        q = q.Where(n => MatchesTab(n.Reason, filter.Tab));

        // My PRs is "PR rows" only — Kind == PullRequest. Comment rows
        // (CR walkthroughs, replies, etc.) and Issue rows are filtered
        // out so the tab shows one row per observed PR state transition
        // (creation, Draft toggle, ready). Legacy rows that pre-date the
        // latest_comment_url snapshot keep their subject.type-derived
        // kind, so PullRequest rows from before Migration v6 still
        // surface in the tab.
        if (filter.Tab == TimelineTab.MyPrs)
        {
            q = q.Where(n => n.Subject.Kind == NotificationEventKind.PullRequest);
        }

        // Repo filter.
        if (!string.IsNullOrEmpty(filter.RepositoryFullName))
        {
            q = q.Where(n => string.Equals(
                n.RepositoryFullName,
                filter.RepositoryFullName,
                StringComparison.OrdinalIgnoreCase));
        }

        // Search across repo / title / reason / subject_type, case-insensitive.
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var needle = filter.SearchText.Trim();
            q = q.Where(n => Contains(n.RepositoryFullName, needle)
                || Contains(n.Subject.Title, needle)
                || Contains(n.Reason.ToString(), needle)
                || Contains(n.Subject.Type, needle));
        }

        // ListByAccountAsync already orders by observed_at DESC; the in-memory
        // filter chain preserves order so newest events stay first.
        return q.ToList();
    }

    public async Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(CancellationToken ct = default)
    {
        var repos = await _repositories.ListByAccountAsync(_accountId, ct).ConfigureAwait(false);
        return repos.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<NotificationEvent> GetPlaceholderItems() =>
        Array.Empty<NotificationEvent>();

    public static bool MatchesTab(NotificationReason reason, TimelineTab tab) => tab switch
    {
        TimelineTab.All => true,
        TimelineTab.Review => reason == NotificationReason.Review,
        TimelineTab.Mention => reason is NotificationReason.Mention or NotificationReason.TeamMention,
        TimelineTab.MyPrs => reason == NotificationReason.MyPr,
        TimelineTab.Watching => reason == NotificationReason.Watching,
        _ => true,
    };

    private static bool Contains(string? haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack))
        {
            return false;
        }

        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
