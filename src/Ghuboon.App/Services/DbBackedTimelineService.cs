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
/// Loads notifications for the configured primary account and applies the
/// <see cref="TimelineFilter"/> in memory: tab filter (Phase 9), repo dropdown,
/// and free-text search.
/// </summary>
public sealed class DbBackedTimelineService : ITimelineService
{
    private readonly INotificationRepository _notifications;
    private readonly IRepositoryRepository _repositories;
    private readonly IAccountRepository _accounts;
    private readonly Func<TimelineItemContext> _itemContextFactory;
    private readonly string _accountId;

    public DbBackedTimelineService(
        INotificationRepository notifications,
        IRepositoryRepository repositories,
        IAccountRepository accounts,
        Func<TimelineItemContext>? itemContextFactory = null,
        string accountId = AppSettingsService.PrimaryAccountId)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _itemContextFactory = itemContextFactory ?? (() => TimelineItemContext.Empty);
        _accountId = accountId;
    }

    public async Task<IReadOnlyList<TimelineItemViewModel>> LoadAsync(TimelineFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var rows = await _notifications.ListByAccountAsync(_accountId, ct).ConfigureAwait(false);

        IEnumerable<GitHubNotification> q = rows;

        // Tab filter.
        q = q.Where(n => MatchesTab(n.Reason, filter.Tab));

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

        var ordered = q.OrderByDescending(n => n.UpdatedAt).ToList();

        var ctx = _itemContextFactory();
        return ordered.Select(n => new TimelineItemViewModel(n, ctx)).ToList();
    }

    public async Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(CancellationToken ct = default)
    {
        var repos = await _repositories.ListByAccountAsync(_accountId, ct).ConfigureAwait(false);
        return repos.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<TimelineItemViewModel> GetPlaceholderItems() =>
        Array.Empty<TimelineItemViewModel>();

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
