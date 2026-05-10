using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghuboon.App.Services;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;
using Serilog;

namespace Ghuboon.App.ViewModels;

/// <summary>
/// Bag of dependencies a real <see cref="TimelineItemViewModel"/> needs.
/// Kept as a record so the App-level composition root can build a single
/// instance and reuse it across rows without ballooning the VM constructor.
/// </summary>
/// <param name="Repository">Local cache repo, used to mirror read-state changes back into SQLite.</param>
/// <param name="Api">Used to push read-state to GitHub.com.</param>
/// <param name="Browser">Used by Open in GitHub.</param>
/// <param name="Clipboard">Used by Copy URL.</param>
/// <param name="Clock">Source of "now" for read timestamps.</param>
/// <param name="PatProvider">
/// Resolves the current PAT from the credential store. Returns null when no PAT
/// is configured; the read commands no-op gracefully in that case.
/// </param>
/// <param name="OnMarkRead">
/// Optional callback invoked after a successful local read transition. The parent
/// <see cref="TimelineViewModel"/> uses this to refresh aggregated unread counts.
/// </param>
/// <param name="Log">Logger; null disables logging from this row.</param>
public sealed record TimelineItemContext(
    INotificationRepository? Repository,
    IGitHubApiClient? Api,
    IBrowserService? Browser,
    IClipboardService? Clipboard,
    IClock? Clock,
    Func<CancellationToken, Task<string?>>? PatProvider,
    Action<TimelineItemViewModel>? OnMarkRead,
    ILogger? Log)
{
    public static TimelineItemContext Empty { get; } =
        new(null, null, null, null, null, null, null, null);
}

/// <summary>
/// One row in the timeline (Phase 8). Owns its read-state, expansion, and
/// per-row commands (Open in GitHub / Mark as read / Copy URL / Toggle expand).
/// Read transitions are idempotent (Phase 10).
/// </summary>
public partial class TimelineItemViewModel : ViewModelBase
{
    private readonly TimelineItemContext _ctx;

    [ObservableProperty]
    private bool _unread;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string? _flashMessage;

    /// <summary>
    /// Build from a fully-populated cache entity (production path).
    /// </summary>
    public TimelineItemViewModel(GitHubNotification source, TimelineItemContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ctx = context ?? TimelineItemContext.Empty;

        Id = source.Id;
        ThreadId = source.ThreadId;
        AccountId = source.AccountId;
        RepositoryFullName = source.RepositoryFullName;
        Title = source.Subject.Title;
        Reason = source.Reason;
        ReasonRaw = source.Reason.ToString();
        SubjectType = source.Subject.Type;
        WebUrl = source.Subject.WebUrl;
        UpdatedAt = source.UpdatedAt;
        _unread = source.Unread;
    }

    /// <summary>
    /// Legacy / placeholder constructor used by <see cref="StubTimelineService"/>
    /// and snapshot tests. Reason is parsed via
    /// <see cref="NotificationReasonMap.From"/>.
    /// </summary>
    public TimelineItemViewModel(
        string id,
        string repositoryFullName,
        string title,
        string reason,
        DateTimeOffset updatedAt,
        bool unread)
        : this(BuildPlaceholderSource(id, repositoryFullName, title, reason, updatedAt, unread),
               TimelineItemContext.Empty)
    {
        // Keep the original raw string for callers that bound to it (back-compat with tests).
        ReasonRaw = reason ?? string.Empty;
    }

    public static TimelineItemViewModel Placeholder(
        string id,
        string repositoryFullName,
        string title,
        string reason,
        DateTimeOffset updatedAt,
        bool unread)
        => new(id, repositoryFullName, title, reason, updatedAt, unread);

    public string Id { get; }
    public string ThreadId { get; } = string.Empty;
    public string AccountId { get; } = string.Empty;
    public string RepositoryFullName { get; }
    public string Title { get; }
    public NotificationReason Reason { get; }
    public string ReasonRaw { get; private set; }
    public string SubjectType { get; } = string.Empty;
    public string? WebUrl { get; }
    public DateTimeOffset UpdatedAt { get; }

    public string ReasonBadgeText => FormatReasonBadge(Reason);
    public string ReasonBadgeColor => GetBadgeColor(Reason);

    /// <summary>"Just now" / "5m" / "2h" / "3d" / "5w" depending on age.</summary>
    public string RelativeTime => FormatRelative(_ctx.Clock?.UtcNow ?? DateTimeOffset.UtcNow, UpdatedAt);

    /// <summary>Back-compat alias used by older bindings.</summary>
    public string UpdatedRelative => RelativeTime;

    [RelayCommand]
    private async Task MarkAsReadAsync(CancellationToken ct)
    {
        if (!Unread)
        {
            return; // Idempotent.
        }

        var pat = _ctx.PatProvider is null ? null : await _ctx.PatProvider(ct).ConfigureAwait(false);

        try
        {
            if (_ctx.Api is not null && !string.IsNullOrEmpty(pat) && !string.IsNullOrEmpty(ThreadId))
            {
                await _ctx.Api.MarkThreadReadAsync(pat, ThreadId, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Phase 10 acceptance: failed read sync must not crash the app and
            // the next sync reconciles. We surface the failure quietly.
            _ctx.Log?.Warning(ex, "Mark-as-read API call failed for {ThreadId}", ThreadId);
            FlashMessage = "Read sync failed; will retry on next sync.";
            return;
        }

        var now = _ctx.Clock?.UtcNow ?? DateTimeOffset.UtcNow;

        if (_ctx.Repository is not null && !string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(AccountId))
        {
            try
            {
                var updated = new GitHubNotification(
                    Id,
                    AccountId,
                    ThreadId,
                    RepositoryFullName,
                    new NotificationSubject(SubjectType, Title, null, WebUrl),
                    Reason,
                    Unread: false,
                    UpdatedAt,
                    LastReadAt: now);
                await _ctx.Repository.UpsertAsync(updated, string.Empty, now, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ctx.Log?.Warning(ex, "Persisting local read state failed for {Id}", Id);
            }
        }

        Unread = false;
        FlashMessage = null;
        _ctx.OnMarkRead?.Invoke(this);
    }

    [RelayCommand]
    private async Task OpenInGitHubAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(WebUrl))
        {
            _ctx.Browser?.OpenUrl(WebUrl);
        }

        if (Unread)
        {
            await MarkAsReadAsync(ct).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task CopyUrlAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(WebUrl) || _ctx.Clipboard is null)
        {
            return;
        }

        await _ctx.Clipboard.SetTextAsync(WebUrl).ConfigureAwait(false);
        FlashMessage = "Copied";
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }

    /// <summary>
    /// Test/seam helper that formats the relative-time bucket ("2m" / "1h" / "3d" / "5w")
    /// for an arbitrary <paramref name="reference"/> "now" instant.
    /// </summary>
    public static string FormatRelative(DateTimeOffset now, DateTimeOffset updatedAt)
    {
        var delta = now - updatedAt;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalSeconds < 60)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes}m";
        }

        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours}h";
        }

        if (delta.TotalDays < 7)
        {
            return $"{(int)delta.TotalDays}d";
        }

        return $"{(int)(delta.TotalDays / 7)}w";
    }

    private static string FormatReasonBadge(NotificationReason reason) => reason switch
    {
        NotificationReason.Review => "REVIEW",
        NotificationReason.Mention => "MENTION",
        NotificationReason.TeamMention => "TEAM",
        NotificationReason.Assigned => "ASSIGN",
        NotificationReason.MyPr => "AUTHOR",
        NotificationReason.Comment => "COMMENT",
        NotificationReason.State => "STATE",
        NotificationReason.Watching => "WATCH",
        NotificationReason.Manual => "MANUAL",
        NotificationReason.Invitation => "INVITE",
        NotificationReason.SecurityAlert => "SECURITY",
        NotificationReason.CiActivity => "CI",
        _ => "OTHER",
    };

    private static string GetBadgeColor(NotificationReason reason) => reason switch
    {
        NotificationReason.Review => "#1F6FEB",
        NotificationReason.Mention => "#A371F7",
        NotificationReason.TeamMention => "#A371F7",
        NotificationReason.Assigned => "#FB8500",
        NotificationReason.MyPr => "#1A7F37",
        NotificationReason.Comment => "#6E7781",
        NotificationReason.State => "#8250DF",
        NotificationReason.Watching => "#6E7781",
        NotificationReason.SecurityAlert => "#CF222E",
        NotificationReason.CiActivity => "#9A6700",
        _ => "#6E7781",
    };

    private const string PlaceholderAccountId = "placeholder";
    private const string PlaceholderRepositoryFullName = "placeholder/placeholder";

    private static GitHubNotification BuildPlaceholderSource(
        string id,
        string repositoryFullName,
        string title,
        string reason,
        DateTimeOffset updatedAt,
        bool unread)
    {
        var parsed = NotificationReasonMap.From(reason);
        // GitHubNotification invariants (Phase 2 hardening) require non-empty
        // Id/AccountId/ThreadId and Id == "{AccountId}:{ThreadId}". The legacy
        // placeholder ctor predates those invariants; synthesize a deterministic
        // synthetic AccountId/ThreadId so the legacy callers keep working.
        var safeId = string.IsNullOrWhiteSpace(id) ? "placeholder" : id;
        var threadId = safeId;
        var compositeId = $"{PlaceholderAccountId}:{threadId}";
        var safeRepo = string.IsNullOrWhiteSpace(repositoryFullName)
            ? PlaceholderRepositoryFullName
            : repositoryFullName;
        // If the caller-supplied repo is malformed for the new invariants
        // ("owner/name", exactly one slash, both sides non-empty), fall back to
        // a safe sentinel so design-time/legacy paths keep working.
        if (!IsValidRepositoryFullName(safeRepo))
        {
            safeRepo = PlaceholderRepositoryFullName;
        }
        return new GitHubNotification(
            compositeId,
            AccountId: PlaceholderAccountId,
            ThreadId: threadId,
            RepositoryFullName: safeRepo,
            Subject: new NotificationSubject("PullRequest", title ?? string.Empty, null, null),
            Reason: parsed,
            Unread: unread,
            UpdatedAt: updatedAt,
            LastReadAt: null);
    }

    private static bool IsValidRepositoryFullName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var slash = value.IndexOf('/');
        if (slash <= 0 || slash != value.LastIndexOf('/') || slash == value.Length - 1)
        {
            return false;
        }

        var owner = value.AsSpan(0, slash);
        var name = value.AsSpan(slash + 1);
        return !owner.IsWhiteSpace() && !name.IsWhiteSpace();
    }
}
