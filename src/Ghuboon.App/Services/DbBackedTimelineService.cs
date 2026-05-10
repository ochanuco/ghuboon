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
///
/// Issue #8: returns domain <see cref="GitHubNotification"/> rows. The
/// presentation layer is responsible for mapping to view-models. The
/// per-row <see cref="TimelineItemContext"/> factory is preserved for backwards
/// compatibility but is no longer used internally; callers that previously
/// passed a factory can drop it.
/// </summary>
public sealed class DbBackedTimelineService : ITimelineService
{
    private readonly INotificationRepository _notifications;
    private readonly IRepositoryRepository _repositories;
    private readonly IAccountRepository _accounts;
    private readonly string _accountId;

    /// <summary>
    /// Optional factory for the per-row VM context. Retained so existing callers
    /// (App composition root, tests) can continue to wire one through; the
    /// <see cref="TimelineViewModel"/> reads it via <see cref="ItemContextFactory"/>
    /// to attach commands to the rows it builds.
    /// </summary>
    public Func<TimelineItemContext> ItemContextFactory { get; }

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
        ItemContextFactory = itemContextFactory ?? (() => TimelineItemContext.Empty);
        _accountId = accountId;
    }

    public async Task<IReadOnlyList<GitHubNotification>> LoadAsync(TimelineFilter filter, CancellationToken ct = default)
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

        return q.OrderByDescending(n => n.UpdatedAt).ToList();
    }

    public async Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(CancellationToken ct = default)
    {
        var repos = await _repositories.ListByAccountAsync(_accountId, ct).ConfigureAwait(false);
        return repos.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<GitHubNotification> GetPlaceholderItems() =>
        Array.Empty<GitHubNotification>();

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
