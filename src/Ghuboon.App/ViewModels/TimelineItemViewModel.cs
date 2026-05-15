using System;
using System.Collections.Generic;
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
/// <param name="EventRepository">
/// Optional event-log repo: when set, a successful mark-as-read also flips
/// every sibling event row for the same thread, so the timeline UI does not
/// keep showing past observations as unread after the user resolves a thread.
/// </param>
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
public enum BodyBlockKind
{
    Markdown,
    Details,
}

public sealed record BodyBlock(BodyBlockKind Kind, string Markdown, string? Summary);

public sealed record TimelineItemContext(
    INotificationRepository? Repository,
    INotificationEventRepository? EventRepository,
    IGitHubApiClient? Api,
    IBrowserService? Browser,
    IClipboardService? Clipboard,
    IClock? Clock,
    Func<CancellationToken, Task<string?>>? PatProvider,
    Action<TimelineItemViewModel>? OnMarkRead,
    ILogger? Log,
    IBookmarkRepository? Bookmarks = null,
    Action<string>? OnFlash = null)
{
    public static TimelineItemContext Empty { get; } =
        new(null, null, null, null, null, null, null, null, null, null, null);
}

/// <summary>
/// One row in the timeline (Phase 8). Owns its read-state, expansion, and
/// per-row commands (Open in GitHub / Mark as read / Copy URL / Toggle expand).
/// Read transitions are idempotent (Phase 10).
/// </summary>
public partial class TimelineItemViewModel : ViewModelBase
{
    private readonly TimelineItemContext _ctx;

    // Issue #26: in-flight guard for the mark-as-read flow. Set immediately
    // after the unread check using Interlocked so a concurrent click cannot
    // slip past while the first call is still awaiting the API.
    private int _markAsReadInFlight;

    [ObservableProperty]
    private bool _unread;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string? _flashMessage;

    /// <summary>
    /// Set <see cref="FlashMessage"/> and also surface the message to the
    /// window status bar via <see cref="TimelineItemContext.OnFlash"/>.
    /// User-facing acks like "Copied" / "Bookmarked" / read-sync failure
    /// flow through here so the user sees them in a single, stable
    /// location instead of (or in addition to) the per-row hint.
    /// </summary>
    private void Flash(string? message)
    {
        FlashMessage = message;
        if (!string.IsNullOrEmpty(message))
        {
            _ctx.OnFlash?.Invoke(message);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBodyAfterLoad))]
    private string? _body;

    // BodyBlocks used to be a getter that re-ran SplitIntoBlocks on every
    // call. Worse, ItemsControl read it once per DataContext change AND
    // every body arrival re-parsed on the UI thread, synchronously
    // feeding Markdown.Avalonia's heavy rendering pass.
    //
    // Caching strategy:
    //   * When Body is set via the setter (typical for the
    //     Task.Run-backed lazy load path), OnBodyChanged parses
    //     SYNCHRONOUSLY on the caller's thread — off-UI for lazy loads
    //     — and stores the result, so the UI thread only sees the
    //     finished list when the binding marshals through.
    //   * When the constructor hydrates _body directly (Migration v8
    //     cached body), we DON'T pre-parse 200 rows on the TL reload's
    //     UI thread. We mark the cache dirty; the getter parses on first
    //     access for that specific row only. That trades a 200-row
    //     hydration hang for a ~1 ms parse on the first focus of each
    //     cached row.
    private IReadOnlyList<BodyBlock> _bodyBlocks = Array.Empty<BodyBlock>();
    private bool _bodyBlocksDirty;
    public IReadOnlyList<BodyBlock> BodyBlocks
    {
        get
        {
            if (_bodyBlocksDirty)
            {
                _bodyBlocks = SplitIntoBlocks(_body);
                _bodyBlocksDirty = false;
            }
            return _bodyBlocks;
        }
        private set
        {
            if (!ReferenceEquals(_bodyBlocks, value))
            {
                _bodyBlocks = value;
                _bodyBlocksDirty = false;
                OnPropertyChanged(nameof(BodyBlocks));
                OnPropertyChanged(nameof(RenderableBlocks));
            }
        }
    }

    partial void OnBodyChanged(string? value)
    {
        BodyBlocks = SplitIntoBlocks(value);
    }

    // Markdown rendering is heavy (third-party MarkdownScrollViewer
    // parses + lays out on the UI thread when its Markdown property is
    // set). Holding A/S through cached rows used to trigger that
    // ~1 s pipeline per row, showing as a frozen UI plus "bouncing"
    // selection highlight when queued keystrokes drained. We defer the
    // ItemsControl binding via this flag: TimelineViewModel flips it
    // back true ~200 ms after a selection settles, so transient
    // intermediate rows never pay the markdown render cost.
    private bool _renderingAllowed;
    public bool RenderingAllowed
    {
        get => _renderingAllowed;
        set
        {
            if (_renderingAllowed != value)
            {
                _renderingAllowed = value;
                OnPropertyChanged(nameof(RenderingAllowed));
                OnPropertyChanged(nameof(RenderableBlocks));
            }
        }
    }

    /// <summary>
    /// What the detail-pane ItemsControl actually binds to. Returns
    /// the parsed BodyBlocks once <see cref="RenderingAllowed"/> is
    /// true; while the selection is settling, returns an empty list so
    /// the markdown control sees no work.
    /// </summary>
    public IReadOnlyList<BodyBlock> RenderableBlocks =>
        RenderingAllowed ? BodyBlocks : Array.Empty<BodyBlock>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBodyAfterLoad))]
    private bool _isLoadingBody;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBodyAfterLoad))]
    private bool _bodyLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommentAuthorBadge))]
    private string? _bodyAuthorLogin;

    /// <summary>
    /// Per-event GitHub login of the actor whose action produced the row
    /// (e.g. <c>"coderabbitai[bot]"</c>). Hydrated from the persisted event /
    /// notification snapshot, then lazily back-filled on row selection when
    /// the body fetch surfaces the latest comment author. The UI prefers this
    /// over <see cref="OwnerLogin"/> via <see cref="DisplayUserLogin"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayUserLogin))]
    [NotifyPropertyChangedFor(nameof(CommentAuthorBadge))]
    private string? _actorLogin;

    /// <summary>
    /// True when this row IS the currently-selected row. Set by
    /// <see cref="TimelineViewModel"/> on selection change. Drives the
    /// blue selection tint that wins over every other row color.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowBackgroundColor))]
    private bool _isSelectedRow;

    /// <summary>
    /// True when this row shares its <see cref="NotificationId"/> with the
    /// currently-focused row. Drives the "related thread" tint so the user
    /// can see at a glance which TL rows belong to the same PR / Issue.
    /// Set by <see cref="TimelineViewModel"/> on selection change.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowBackgroundColor))]
    private bool _isRelatedToFocus;

    /// <summary>
    /// True when the user has bookmarked this thread. Driven by the
    /// Bookmarks tab filter and the Shift+S / Shift+Cmd+S shortcuts.
    /// Hydrated from <see cref="IBookmarkRepository"/> on each timeline
    /// reload so the flag survives sync.
    /// </summary>
    [ObservableProperty]
    private bool _isBookmarked;

    /// <summary>
    /// Background tint for the timeline row, in priority order:
    ///   1. Selected row → blue (always wins).
    ///   2. Mention / TeamMention → soft red (someone called you out).
    ///   3. Review request → soft orange (a review is waiting on you,
    ///      separate from mention so the two action types can be
    ///      distinguished at a glance).
    ///   4. Same-thread sibling of the focused row → soft green.
    ///   5. Otherwise transparent.
    /// </summary>
    public string RowBackgroundColor =>
        IsSelectedRow ? "#BBDEFB"
        : Reason switch
        {
            NotificationReason.Mention => "#FFEBEE",
            NotificationReason.TeamMention => "#FFEBEE",
            NotificationReason.Review => "#FFF3E0",
            _ when IsRelatedToFocus => "#E8F5E9",
            _ => "Transparent",
        };

    private bool _bodyAttempted;

    /// <summary>
    /// True once body fetch has finished and produced no content. Used by the
    /// detail pane to show "(no description)" rather than a blank space.
    /// </summary>
    public bool HasNoBodyAfterLoad => BodyLoaded && !IsLoadingBody && string.IsNullOrEmpty(Body);

    /// <summary>
    /// Build from a fully-populated cache entity (production path).
    /// </summary>
    public TimelineItemViewModel(GitHubNotification source, TimelineItemContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ctx = context ?? TimelineItemContext.Empty;

        Id = source.Id;
        NotificationId = source.Id;
        ThreadId = source.ThreadId;
        AccountId = source.AccountId;
        RepositoryFullName = source.RepositoryFullName;
        Title = source.Subject.Title;
        Reason = source.Reason;
        ReasonRaw = source.Reason.ToString();
        SubjectType = source.Subject.Type;
        WebUrl = source.Subject.WebUrl;
        SubjectApiUrl = source.Subject.ApiUrl;
        LatestCommentApiUrl = source.Subject.LatestCommentApiUrl;
        EventKind = source.Subject.Kind;
        UpdatedAt = source.UpdatedAt;
        _unread = source.Unread;
        _actorLogin = source.ActorLogin;
    }

    /// <summary>
    /// Build from an event-log row (event-log timeline path). Each event is its
    /// own row, so <see cref="Id"/> is the event id (prefixed with <c>"evt:"</c>)
    /// rather than the notification id — this stops Avalonia's ListBox key
    /// tracking from collapsing two rows that happen to share a thread.
    /// <see cref="NotificationId"/> still carries the underlying thread's
    /// notification id so mark-as-read can flip every sibling event.
    /// </summary>
    public TimelineItemViewModel(NotificationEvent source, TimelineItemContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ctx = context ?? TimelineItemContext.Empty;

        // Use the event's local autoincrement id so two rows for the same
        // thread don't get treated as duplicates by the ListBox virtualization
        // layer. NotificationId stays equal to the underlying thread's
        // notification id so mark-as-read can flip every sibling event.
        Id = source.Id > 0 ? $"evt:{source.Id}" : source.NotificationId;
        NotificationId = source.NotificationId;
        ThreadId = source.ThreadId;
        AccountId = source.AccountId;
        RepositoryFullName = source.RepositoryFullName;
        Title = source.Subject.Title;
        Reason = source.Reason;
        ReasonRaw = source.Reason.ToString();
        SubjectType = source.Subject.Type;
        WebUrl = source.Subject.WebUrl;
        SubjectApiUrl = source.Subject.ApiUrl;
        LatestCommentApiUrl = source.Subject.LatestCommentApiUrl;
        EventKind = source.Subject.Kind;
        // Display the upstream updated_at for this observation: that is the
        // "when did this event happen" timestamp users expect on a per-row
        // event log (not when our sync wrote the row).
        UpdatedAt = source.SourceUpdatedAt;
        _unread = source.Unread;
        _actorLogin = source.ActorLogin;
        // Hydrate from the per-event body cache (Migration v8). When the
        // event already carries a fetched body we render from cache and
        // skip the API call entirely on the next selection. We mark
        // BodyBlocks as dirty (lazy-parse on first access) rather than
        // pre-computing here — the constructor runs on the UI thread
        // during TimelineViewModel.ReloadAsync's foreach, and pre-
        // parsing 200 cached rows would stall the reload visibly.
        if (!string.IsNullOrEmpty(source.Body))
        {
            _body = source.Body;
            _bodyAuthorLogin = source.BodyAuthorLogin;
            _bodyBlocksDirty = true;
            _bodyLoaded = true;
            _bodyAttempted = true;
        }
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

    /// <summary>
    /// Underlying notification id ("{AccountId}:{ThreadId}") for this row. When
    /// the row is built from an event, multiple rows can share this id but
    /// have distinct <see cref="Id"/> values (event-id prefixed with "evt:").
    /// </summary>
    public string NotificationId { get; } = string.Empty;
    public string ThreadId { get; } = string.Empty;
    public string AccountId { get; } = string.Empty;
    public string RepositoryFullName { get; }
    /// <summary>
    /// Owner segment of <see cref="RepositoryFullName"/> ("ochanuco" for
    /// "ochanuco/ghuboon"). Until we surface the per-event actor, the owner
    /// is the most useful "user" the timeline can show without an extra API
    /// fetch per row.
    /// </summary>
    public string OwnerLogin
    {
        get
        {
            var slash = RepositoryFullName?.IndexOf('/') ?? -1;
            return slash > 0 ? RepositoryFullName![..slash] : (RepositoryFullName ?? string.Empty);
        }
    }

    /// <summary>
    /// What the timeline's User column actually shows. Prefers the per-event
    /// <see cref="ActorLogin"/> when known (e.g. <c>"coderabbitai[bot]"</c>),
    /// falls back to <see cref="OwnerLogin"/> for cold-cache rows that
    /// haven't been selected yet (the listing API doesn't include the actor).
    /// </summary>
    public string DisplayUserLogin =>
        string.IsNullOrEmpty(ActorLogin) ? OwnerLogin : ActorLogin!;

    /// <summary>
    /// Detail-pane sub-line shown next to the PR creator. When the body
    /// being displayed is a comment (BodyAuthorLogin differs from the
    /// thread owner ActorLogin), surfaces the commenter as e.g.
    /// <c>"💬 @coderabbitai[bot]"</c>. Returns empty when the body author
    /// is the same as the PR creator (no need to repeat the same name).
    /// </summary>
    public string CommentAuthorBadge =>
        !string.IsNullOrEmpty(BodyAuthorLogin)
        && !string.Equals(BodyAuthorLogin, ActorLogin, StringComparison.Ordinal)
            ? $"💬 @{BodyAuthorLogin}"
            : string.Empty;

    /// <summary>
    /// Numeric local autoincrement id of the underlying notification_events
    /// row, surfaced for debug ("which row is this?"). Returns null for the
    /// non-event-backed legacy paths (placeholders, design-time stubs).
    /// </summary>
    public long? EventLocalId
    {
        get
        {
            const string prefix = "evt:";
            if (Id.StartsWith(prefix, StringComparison.Ordinal)
                && long.TryParse(Id.AsSpan(prefix.Length), out var n))
            {
                return n;
            }
            return null;
        }
    }
    public string Title { get; }
    public NotificationReason Reason { get; }
    public string ReasonRaw { get; private set; }
    public string SubjectType { get; } = string.Empty;
    public string? WebUrl { get; }
    public string? SubjectApiUrl { get; }

    /// <summary>
    /// Snapshot of <c>subject.latest_comment_url</c> at the time this row
    /// was observed. Drives the EventKind classification (Comment vs PR/
    /// Issue/...) and the per-event actor / body fetch URL.
    /// </summary>
    public string? LatestCommentApiUrl { get; }

    /// <summary>
    /// What this row represents in GitHub terms (PR / Comment / Issue / ...).
    /// Computed from <see cref="NotificationSubject.Kind"/> at construction.
    /// Drives the kind badge and the actor / body resolution path.
    /// </summary>
    public NotificationEventKind EventKind { get; } = NotificationEventKind.Unknown;

    public DateTimeOffset UpdatedAt { get; }

    public string ReasonBadgeText => FormatReasonBadge(Reason);
    public string ReasonBadgeColor => GetBadgeColor(Reason);

    /// <summary>"PR" / "COMMENT" / "ISSUE" / ... — primary type label.</summary>
    public string KindBadgeText => FormatKindBadge(EventKind);
    public string KindBadgeColor => GetKindColor(EventKind);

    /// <summary>"Just now" / "5m" / "2h" / "3d" / "5w" depending on age.</summary>
    public string RelativeTime => FormatRelative(_ctx.Clock?.UtcNow ?? DateTimeOffset.UtcNow, UpdatedAt);

    /// <summary>Back-compat alias used by older bindings.</summary>
    public string UpdatedRelative => RelativeTime;

    // Issue #26: serialize concurrent MarkAsRead invocations. Without the
    // in-flight flag below, a second click slips past the `if (!Unread)` guard
    // while the first call is still awaiting the API call, hitting GitHub twice
    // for the same thread. AllowConcurrentExecutions=false also stops command
    // re-entrancy at the binding level for keyboard / button mashing.
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task MarkAsReadAsync(CancellationToken ct)
    {
        if (!Unread)
        {
            return; // Idempotent.
        }

        // Try-acquire the in-flight slot. If another invocation already owns
        // it we treat this as a no-op; the in-flight call will flip Unread,
        // and any further calls will short-circuit on the !Unread guard above.
        if (Interlocked.CompareExchange(ref _markAsReadInFlight, 1, 0) != 0)
        {
            return;
        }

        // Optimistic flip: Unread / OnMarkRead fire IMMEDIATELY on the
        // calling (UI) thread before any await, so the unread dot and the
        // aggregated counter update without waiting on the network and DB.
        // The independent I/O paths (API + notifications row + event-log
        // siblings) then run in parallel. A failed API call reverts Unread
        // and surfaces a flash; local DB failures stay non-fatal.
        //
        // The flip happens BEFORE the first await so the Keychain PAT
        // lookup (~tens of ms per call on macOS) doesn't introduce a
        // visible delay between keypress and unread dot disappearing.
        // If the prerequisites turn out to be missing (no PAT / Api /
        // ThreadId) or the API call fails, the flip is reverted below.
        var canRevert = Unread;
        var now = _ctx.Clock?.UtcNow ?? DateTimeOffset.UtcNow;
        Unread = false;
        FlashMessage = null;
        _ctx.OnMarkRead?.Invoke(this);

        try
        {
            var pat = _ctx.PatProvider is null ? null : await _ctx.PatProvider(ct).ConfigureAwait(true);

            // Without a working PAT / API / ThreadId the local cache
            // would drift ahead of GitHub's actual state and stay
            // marked-read even though we never PATCH'd /notifications,
            // so revert the optimistic flip and surface a hint.
            var canPushUpstream = _ctx.Api is not null
                && !string.IsNullOrEmpty(pat)
                && !string.IsNullOrEmpty(ThreadId);

            if (!canPushUpstream)
            {
                _ctx.Log?.Information("Mark-as-read skipped: missing PAT / Api / ThreadId for {NotificationId}", NotificationId);
                if (canRevert) Unread = true;
                Flash("Sign in to mark read on GitHub; local state unchanged.");
                return;
            }

            // API first — if it fails, revert the optimistic flip and skip
            // local DB writes so the cache stays in sync with the (still-
            // unread) upstream state. ADR-014: read-sync failures degrade
            // gracefully; the next sync will reconcile.
            try
            {
                await _ctx.Api!.MarkThreadReadAsync(pat!, ThreadId, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _ctx.Log?.Warning(ex, "Mark-as-read API call failed for {ThreadId}", ThreadId);
                Unread = true;
                Flash("Read sync failed; will retry on next sync.");
                return;
            }

            // DB writes run in parallel because they're independent paths
            // (notifications row vs event-log siblings) and we've already
            // confirmed upstream success.
            //
            // Use the targeted SetReadStateAsync so only `unread` and
            // `last_read_at` are touched; the previous full UpsertAsync
            // rebuild had stripped subject_api_url / latest_comment_url /
            // raw_json to nulls and broken the body fetch + Kind
            // classification on the next render.
            Task notifTask = Task.CompletedTask;
            if (_ctx.Repository is not null && !string.IsNullOrEmpty(NotificationId))
            {
                notifTask = SwallowAsync(
                    _ctx.Repository.SetReadStateAsync(NotificationId, unread: false, readAt: now, ct),
                    "Persisting local read state failed for " + NotificationId);
            }

            Task eventTask = Task.CompletedTask;
            if (_ctx.EventRepository is not null && !string.IsNullOrEmpty(NotificationId) && !string.IsNullOrEmpty(AccountId))
            {
                eventTask = SwallowAsync(
                    _ctx.EventRepository.MarkThreadAsReadAsync(AccountId, NotificationId, now, ct),
                    "Marking event-log siblings read failed for " + NotificationId);
            }

            await Task.WhenAll(notifTask, eventTask).ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Exchange(ref _markAsReadInFlight, 0);
        }
    }

    /// <summary>
    /// Swallow exceptions on a background task so a single sub-task failure
    /// doesn't fault the WhenAll. Each callsite already passes a per-task
    /// log message for diagnostics.
    /// </summary>
    private async Task SwallowAsync(Task task, string failureMessage)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // not surfaced
        }
        catch (Exception ex)
        {
            _ctx.Log?.Warning(ex, "{Message}", failureMessage);
        }
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
        Flash("Copied");
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    private async Task CopyEventIdAsync()
    {
        if (_ctx.Clipboard is null) return;
        var label = EventLocalId is { } n
            ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Id;
        await _ctx.Clipboard.SetTextAsync(label).ConfigureAwait(false);
        Flash($"Copied event id {label}");
    }

    /// <summary>
    /// Bookmark this thread so it shows in the Bookmarks tab. Optimistic:
    /// we flip <see cref="IsBookmarked"/> on the UI thread first, then
    /// persist; if the DB write fails we log but don't revert because the
    /// next reload reads from the DB and reconciles automatically.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task BookmarkAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(NotificationId) || string.IsNullOrEmpty(AccountId)) return;
        if (_ctx.Bookmarks is null) return;
        if (IsBookmarked) return; // idempotent

        IsBookmarked = true;
        Flash("Bookmarked");
        try
        {
            var now = _ctx.Clock?.UtcNow ?? DateTimeOffset.UtcNow;
            await _ctx.Bookmarks.SetAsync(AccountId, NotificationId, now, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _ctx.Log?.Warning(ex, "Persisting bookmark failed for {NotificationId}", NotificationId);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task UnbookmarkAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(NotificationId) || string.IsNullOrEmpty(AccountId)) return;
        if (_ctx.Bookmarks is null) return;
        if (!IsBookmarked) return; // idempotent

        IsBookmarked = false;
        Flash("Bookmark removed");
        try
        {
            await _ctx.Bookmarks.ClearAsync(AccountId, NotificationId, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _ctx.Log?.Warning(ex, "Clearing bookmark failed for {NotificationId}", NotificationId);
        }
    }

    /// <summary>
    /// Diagnostic hook used by TimelineViewModel.OnSelectedItemChanged
    /// to record how long the synchronous selection-change handler took.
    /// Routed through the per-row Log so it lands in the same file sink
    /// as the body-load timings, giving us a single place to look when
    /// the user reports a UI hang on focus.
    /// </summary>
    public void LogSelectionTiming(long totalMs, long tintMs)
    {
        if (totalMs < 30 && tintMs < 30) return; // skip noise
        _ctx.Log?.Information(
            "select.handled id={NotificationId} totalMs={TotalMs} tintMs={TintMs} bodyCached={BodyCached}",
            NotificationId, totalMs, tintMs, BodyLoaded);
    }

    /// <summary>
    /// Lazy-fetch the PR/Issue/Comment body from GitHub for the detail panel.
    /// Idempotent — guarded by <c>_bodyAttempted</c> so repeat selection of the
    /// same row doesn't re-hit the API. Failures are silent (Body stays null).
    /// </summary>
    public async Task EnsureBodyLoadedAsync(CancellationToken ct = default)
    {
        if (_bodyAttempted) return;
        _bodyAttempted = true;

        // Hard deadline independent of HttpClient.Timeout. The handler-
        // level timeout uses System.Threading.Timer which got disrupted
        // across macOS sleep on 2026-05-14, so an HTTP request that
        // straddled sleep could hang the body load forever — the UI
        // stayed pinned at "loading" with no resolution. CancelAfter
        // here is a backup deadline rooted in our linked CTS; if the
        // chain isn't done in 10 s we abandon, mark BodyLoaded so the
        // detail pane settles, and the next selection of the same row
        // will retry (we reset _bodyAttempted on cancellation).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var lct = deadline.Token;
        // Diagnostic: time each body-load attempt end-to-end so we can
        // attribute the user-reported "Detail loading hang on uncached
        // focus" to a specific stage (DB read, PAT, HTTP, parse).
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await EnsureBodyLoadedCoreAsync(lct).ConfigureAwait(true);
            // Suppress body.load.done when the linked CTS fired — the
            // inner catch swallows OCE on cancellation and returns
            // normally, which would otherwise log "done" for what was
            // actually an aborted load and skew the diagnostic numbers.
            if (!lct.IsCancellationRequested)
            {
                _ctx.Log?.Information("body.load.done id={NotificationId} totalMs={Ms} bodyLen={Len}",
                    NotificationId, totalSw.ElapsedMilliseconds, _body?.Length ?? 0);
            }
        }
        catch (OperationCanceledException)
        {
            // Catch both deadline-triggered and user-triggered cancellation
            // so neither escapes the fire-and-forget Task.Run from the
            // selection-changed handler (where escapes become unobserved
            // task exceptions). Inner EnsureBodyLoadedCoreAsync already
            // settles state in its own OCE catch; this is defense in
            // depth for the path where the inner catch's filter rejects
            // (e.g., future code that doesn't re-link the inner ct).
            _bodyAttempted = false;
            IsLoadingBody = false;
            BodyLoaded = true;
            if (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _ctx.Log?.Information("EnsureBodyLoadedAsync deadline (10 s) for {NotificationId}", NotificationId);
            }
        }
    }

    private async Task EnsureBodyLoadedCoreAsync(CancellationToken ct)
    {

        // Fallback chain for legacy rows that lost SubjectApiUrl:
        //  1) read the canonical notifications row from the cache
        //  2) re-fetch via GET /notifications/threads/{thread_id} (reliable
        //     even when the cache lost it on the first sync)
        var apiUrl = SubjectApiUrl;
        if (string.IsNullOrEmpty(apiUrl) && _ctx.Repository is { } repo)
        {
            try
            {
                var canonical = await repo.GetByIdAsync(NotificationId, ct).ConfigureAwait(true);
                apiUrl = canonical?.Subject.ApiUrl;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { _bodyAttempted = false; throw; }
            catch { /* fallthrough to the network probe */ }
        }

        if (string.IsNullOrEmpty(apiUrl)
            && _ctx.Api is { } api
            && _ctx.PatProvider is { } provider
            && !string.IsNullOrEmpty(ThreadId))
        {
            try
            {
                var pat = await provider(ct).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(pat))
                {
                    apiUrl = await api.GetThreadSubjectUrlAsync(pat, ThreadId, ct).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { _bodyAttempted = false; throw; }
            catch { /* swallowed; we'll just show "(no description)" below */ }
        }

        if (_ctx.Api is null || _ctx.PatProvider is null || string.IsNullOrEmpty(apiUrl))
        {
            BodyLoaded = true;
            return;
        }

        try
        {
            IsLoadingBody = true;
            var pat = await _ctx.PatProvider(ct).ConfigureAwait(true);
            if (string.IsNullOrEmpty(pat))
            {
                BodyLoaded = true;
                return;
            }

            // EventKind drives both the displayed body AND the actor:
            //   * Comment kind  → fetch via latest_comment_url; actor =
            //                     commenter, body = comment text.
            //   * PR/Issue/...  → fetch via subject.url; actor = subject
            //                     creator, body = description.
            // The kind is computed at sync time from latest_comment_url, so
            // each row carries the right classification without re-fetching.
            // For legacy rows (latest_comment_url = NULL pre-Migration v6)
            // the kind defaults to the subject type and we hit subject.url,
            // matching the historical "show description" behavior.
            string? targetUrl;
            if (EventKind == NotificationEventKind.Comment
                && !string.IsNullOrEmpty(LatestCommentApiUrl))
            {
                targetUrl = LatestCommentApiUrl;
            }
            else
            {
                targetUrl = apiUrl;
            }

            var fetched = await _ctx.Api
                .GetSubjectBodyAndAuthorAsync(pat, targetUrl, ct)
                .ConfigureAwait(true);

            // Comment events fall back to a thread-level latest-comment
            // probe when the per-event /comments/{id} URL is missing
            // (legacy rows lacked the snapshot). The thread metadata
            // endpoint always returns SOMETHING current, which is the
            // best we can do without per-event provenance.
            var content = fetched.Body;
            var bodyAuthor = fetched.AuthorLogin;
            if (string.IsNullOrEmpty(content)
                && EventKind == NotificationEventKind.Comment
                && !string.IsNullOrEmpty(ThreadId))
            {
                var probe = await _ctx.Api
                    .GetLatestCommentDetailsAsync(pat, ThreadId, ct)
                    .ConfigureAwait(true);
                content = probe.Body;
                bodyAuthor = probe.AuthorLogin;
            }

            Body = StripHtmlComments(content);
            BodyAuthorLogin = bodyAuthor;

            // Persist the body to the per-event cache so subsequent renders
            // (next reload, future sessions) read from the local DB and
            // don't re-hit the GitHub API for the same row. Only writes
            // when we actually got content; an empty fetch leaves the row
            // null so the next selection retries.
            if (!string.IsNullOrEmpty(Body)
                && _ctx.EventRepository is { } bodyRepo
                && EventLocalId is { } bodyEventId)
            {
                try
                {
                    await bodyRepo.SetBodyAsync(bodyEventId, Body, BodyAuthorLogin, ct).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _ctx.Log?.Information(ex, "Persisting body for event {EventId} failed (non-fatal)", bodyEventId);
                }
            }

            // ActorLogin = the actor of THIS row's content (commenter for
            // Comment kind, creator for PR/Issue kind). Always overwrite
            // so stale values from earlier sessions get corrected.
            if (!string.IsNullOrEmpty(bodyAuthor))
            {
                var authorLogin = bodyAuthor;
                ActorLogin = authorLogin;

                if (_ctx.EventRepository is { } evRepo && EventLocalId is { } eventId)
                {
                    try
                    {
                        await evRepo.SetActorLoginAsync(eventId, authorLogin!, ct).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        _ctx.Log?.Information(ex, "Persisting actor_login for event {EventId} failed (non-fatal)", eventId);
                    }
                }

                if (_ctx.Repository is { } notifRepo && !string.IsNullOrEmpty(NotificationId))
                {
                    try
                    {
                        await notifRepo.SetActorLoginAsync(NotificationId, authorLogin!, ct).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        _ctx.Log?.Information(ex, "Persisting actor_login for notification {NotificationId} failed (non-fatal)", NotificationId);
                    }
                }
            }

            BodyLoaded = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation here covers both the outer user CT (selection
            // moved away) AND the 10 s deadline applied in the wrapper.
            // Settle the row in either case: the moved-away row is no
            // longer visible so it doesn't matter, but the deadline case
            // needs BodyLoaded=true so the still-visible row exits the
            // loading spinner state. _bodyAttempted=false allows retry
            // on the next selection.
            _bodyAttempted = false;
            BodyLoaded = true;
        }
        catch (Exception ex)
        {
            _ctx.Log?.Information(ex, "EnsureBodyLoadedAsync failed (non-fatal)");
            // Settle the row so the detail pane doesn't sit on the loading
            // spinner forever after a transient fetch failure. The user can
            // still re-trigger by reloading the timeline; _bodyAttempted
            // stays true so we don't hammer GitHub for a known-bad row.
            BodyLoaded = true;
        }
        finally
        {
            IsLoadingBody = false;
        }
    }

    /// <summary>
    /// Sanitize Markdown before rendering: strip HTML comments and a few
    /// bare HTML wrappers that GitHub bots ship raw, then collapse runs of
    /// blank lines. <c>&lt;details&gt;</c> blocks are NOT touched here —
    /// they're handled by <see cref="SplitIntoBlocks"/>.
    /// </summary>
    public static string? StripHtmlComments(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        var s = markdown!;
        var rx = System.Text.RegularExpressions.RegexOptions.Singleline
                 | System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        var rxLine = System.Text.RegularExpressions.RegexOptions.IgnoreCase
                     | System.Text.RegularExpressions.RegexOptions.Multiline;

        // 1. HTML comments (<!-- ... -->).
        s = System.Text.RegularExpressions.Regex.Replace(s, "<!--.*?-->", string.Empty, rx);

        // 2. drop a few bare wrapper tags. <blockquote>'s content stays.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"</?blockquote[^>]*>", string.Empty, rx);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"</?sub[^>]*>", string.Empty, rx);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"</?sup[^>]*>", string.Empty, rx);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"</?small[^>]*>", string.Empty, rx);

        // 3. collapse 3+ newlines down to a paragraph break.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(\r?\n){3,}", "\n\n", rxLine);

        return s.Trim();
    }

    /// <summary>
    /// Split a Markdown blob into ordered blocks the detail pane renders
    /// individually:
    ///   * <see cref="BodyBlockKind.Markdown"/> — vanilla markdown the
    ///     <c>MarkdownScrollViewer</c> can render.
    ///   * <see cref="BodyBlockKind.Details"/> — a collapsible region; the
    ///     <c>Summary</c> becomes the Expander header and the body becomes
    ///     a nested MarkdownScrollViewer.
    /// Nested details are flattened (each one becomes its own block) — that
    /// matches GitHub's UI which renders them as siblings once expanded.
    /// </summary>
    public static IReadOnlyList<BodyBlock> SplitIntoBlocks(string? sanitized)
    {
        if (string.IsNullOrEmpty(sanitized)) return Array.Empty<BodyBlock>();

        var blocks = new List<BodyBlock>();
        var i = 0;
        while (i < sanitized!.Length)
        {
            var openIdx = FindNextTag(sanitized, i, "<details");
            if (openIdx < 0)
            {
                AddText(blocks, sanitized.Substring(i));
                break;
            }
            if (openIdx > i)
            {
                AddText(blocks, sanitized.Substring(i, openIdx - i));
            }
            // Skip the opening tag, including any attributes up to '>'.
            var openEnd = sanitized.IndexOf('>', openIdx);
            if (openEnd < 0) { AddText(blocks, sanitized.Substring(i)); break; }
            var bodyStart = openEnd + 1;

            var closeIdx = FindBalancedClose(sanitized, bodyStart);
            if (closeIdx < 0)
            {
                // unclosed <details> — keep the rest as plain text.
                AddText(blocks, sanitized.Substring(i));
                break;
            }
            var bodyEnd = closeIdx;
            var afterClose = sanitized.IndexOf('>', closeIdx) + 1;

            var inner = sanitized.Substring(bodyStart, bodyEnd - bodyStart);

            // Pull out the (optional) leading <summary>X</summary>.
            string? summary = null;
            var sumRx = new System.Text.RegularExpressions.Regex(
                @"<summary[^>]*>(?<inner>.*?)</summary>",
                System.Text.RegularExpressions.RegexOptions.Singleline
                | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var sm = sumRx.Match(inner);
            if (sm.Success)
            {
                summary = sm.Groups["inner"].Value.Trim();
                inner = inner.Remove(sm.Index, sm.Length);
            }

            // Recurse on the rest so nested <details> become their own
            // sibling blocks within the parent's expanded body.
            var nested = SplitIntoBlocks(inner);
            blocks.Add(new BodyBlock(BodyBlockKind.Details, JoinBlocks(nested), summary ?? "(details)"));

            i = afterClose > 0 ? afterClose : sanitized.Length;
        }

        return blocks;
    }

    private static int FindNextTag(string s, int start, string tagPrefix)
    {
        // Case-insensitive search for the opening tag prefix (e.g. "<details").
        var idx = start;
        while (idx <= s.Length - tagPrefix.Length)
        {
            if (string.Compare(s, idx, tagPrefix, 0, tagPrefix.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                // Ensure the next char is '>' or whitespace so "<details" doesn't
                // accidentally match "<detailsfoo".
                var next = idx + tagPrefix.Length;
                if (next >= s.Length || s[next] == '>' || char.IsWhiteSpace(s[next]))
                {
                    return idx;
                }
            }
            idx++;
        }
        return -1;
    }

    /// <summary>
    /// Given a position immediately after a <c>&lt;details&gt;</c> opening
    /// tag, return the index of the matching <c>&lt;/details&gt;</c>. Tracks
    /// nested opens so balanced pairs are honored.
    /// </summary>
    private static int FindBalancedClose(string s, int start)
    {
        var depth = 1;
        var idx = start;
        while (idx < s.Length)
        {
            var open = FindNextTag(s, idx, "<details");
            var close = FindNextTag(s, idx, "</details");
            if (close < 0) return -1;

            if (open >= 0 && open < close)
            {
                depth++;
                idx = open + "<details".Length;
            }
            else
            {
                depth--;
                if (depth == 0) return close;
                idx = close + "</details".Length;
            }
        }
        return -1;
    }

    private static void AddText(List<BodyBlock> blocks, string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        blocks.Add(new BodyBlock(BodyBlockKind.Markdown, trimmed, null));
    }

    private static string JoinBlocks(IReadOnlyList<BodyBlock> blocks)
    {
        // For nested-details bodies we just concatenate the markdown
        // content; if there are inner details, they show up as un-expanded
        // headers (we'd need a recursive ItemsControl to fully restore the
        // tree, which is an MVP follow-up).
        var sb = new System.Text.StringBuilder();
        foreach (var b in blocks)
        {
            if (b.Kind == BodyBlockKind.Markdown)
            {
                sb.AppendLine(b.Markdown);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"**{b.Summary}**");
                sb.AppendLine();
                sb.AppendLine(b.Markdown);
                sb.AppendLine();
            }
        }
        return sb.ToString().Trim();
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

    private static string FormatKindBadge(NotificationEventKind kind) => kind switch
    {
        NotificationEventKind.PullRequest => "PR",
        NotificationEventKind.Issue => "ISSUE",
        NotificationEventKind.Comment => "COMMENT",
        NotificationEventKind.Discussion => "DISCUSSION",
        NotificationEventKind.Commit => "COMMIT",
        NotificationEventKind.Release => "RELEASE",
        _ => "OTHER",
    };

    private static string GetKindColor(NotificationEventKind kind) => kind switch
    {
        NotificationEventKind.PullRequest => "#1A7F37",
        NotificationEventKind.Issue => "#1F6FEB",
        NotificationEventKind.Comment => "#6E7781",
        NotificationEventKind.Discussion => "#A371F7",
        NotificationEventKind.Commit => "#9A6700",
        NotificationEventKind.Release => "#8250DF",
        _ => "#6E7781",
    };

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
