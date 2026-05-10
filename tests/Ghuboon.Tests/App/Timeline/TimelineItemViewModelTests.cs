using System;
using System.Linq;
using System.Threading.Tasks;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Timeline;

public class TimelineItemViewModelTests
{
    private static (TimelineItemViewModel vm, FakeNotificationRepository repo, FakeApiClient api,
                    FakeBrowser browser, FakeClipboard clip, FakeClock clock) BuildVm(
        bool unread = true,
        NotificationReason reason = NotificationReason.Mention)
    {
        var repo = new FakeNotificationRepository();
        var api = new FakeApiClient();
        var browser = new FakeBrowser();
        var clip = new FakeClipboard();
        var clock = new FakeClock();

        var src = TimelineTestData.Build(
            id: "primary:1",
            accountId: "primary",
            repo: "octocat/hello",
            title: "Add CONTRIBUTING.md",
            reason: reason,
            unread: unread,
            updatedAt: clock.UtcNow.AddMinutes(-5),
            threadId: "1",
            webUrl: "https://github.com/octocat/hello/pull/1");

        var ctx = new TimelineItemContext(
            Repository: repo,
            EventRepository: null,
            Api: api,
            Browser: browser,
            Clipboard: clip,
            Clock: clock,
            PatProvider: _ => Task.FromResult<string?>("pat"),
            OnMarkRead: null,
            Log: null);

        return (new TimelineItemViewModel(src, ctx), repo, api, browser, clip, clock);
    }

    [Fact]
    public async Task MarkAsRead_FirstCall_UpdatesUnreadAndCallsApi()
    {
        var (vm, repo, api, _, _, _) = BuildVm(unread: true);

        Assert.True(vm.Unread);
        await vm.MarkAsReadCommand.ExecuteAsync(null);

        Assert.False(vm.Unread);
        // Snapshot the bag so ordering / single-element assertions are stable.
        var threads = api.MarkedReadThreads.ToList();
        Assert.Single(threads);
        Assert.Equal("1", threads[0]);
        Assert.Equal(1, repo.UpsertCallCount);
    }

    [Fact]
    public async Task MarkAsRead_SecondCall_NoOp()
    {
        var (vm, repo, api, _, _, _) = BuildVm(unread: true);

        await vm.MarkAsReadCommand.ExecuteAsync(null);
        await vm.MarkAsReadCommand.ExecuteAsync(null);

        Assert.False(vm.Unread);
        Assert.Single(api.MarkedReadThreads);
        Assert.Equal(1, repo.UpsertCallCount);
    }

    [Fact]
    public async Task OpenInGitHub_AlsoMarksRead_WhenUnread()
    {
        var (vm, _, api, browser, _, _) = BuildVm(unread: true);

        await vm.OpenInGitHubCommand.ExecuteAsync(null);

        Assert.Single(browser.OpenedUrls);
        Assert.Equal("https://github.com/octocat/hello/pull/1", browser.OpenedUrls[0]);
        Assert.False(vm.Unread);
        Assert.Single(api.MarkedReadThreads);
    }

    [Fact]
    public async Task OpenInGitHub_DoesNotCallApi_WhenAlreadyRead()
    {
        var (vm, _, api, browser, _, _) = BuildVm(unread: false);

        await vm.OpenInGitHubCommand.ExecuteAsync(null);

        Assert.Single(browser.OpenedUrls);
        Assert.Empty(api.MarkedReadThreads);
    }

    [Fact]
    public async Task CopyUrl_PutsWebUrlOnClipboard()
    {
        var (vm, _, _, _, clip, _) = BuildVm();

        await vm.CopyUrlCommand.ExecuteAsync(null);

        Assert.Single(clip.Texts);
        Assert.Equal("https://github.com/octocat/hello/pull/1", clip.Texts[0]);
        Assert.Equal("Copied", vm.FlashMessage);
    }

    [Fact]
    public async Task MarkAsRead_WhenApiThrows_KeepsItemUnread_AndShowsFlash()
    {
        var (vm, _, api, _, _, _) = BuildVm(unread: true);
        api.MarkReadOverride = _ => new InvalidOperationException("boom");

        await vm.MarkAsReadCommand.ExecuteAsync(null);

        Assert.True(vm.Unread); // unchanged
        Assert.NotNull(vm.FlashMessage);
    }

    [Fact]
    public void RelativeTime_FormatsBuckets()
    {
        var now = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("just now", TimelineItemViewModel.FormatRelative(now, now.AddSeconds(-30)));
        Assert.Equal("2m", TimelineItemViewModel.FormatRelative(now, now.AddMinutes(-2)));
        Assert.Equal("1h", TimelineItemViewModel.FormatRelative(now, now.AddHours(-1)));
        Assert.Equal("3d", TimelineItemViewModel.FormatRelative(now, now.AddDays(-3)));
        Assert.Equal("2w", TimelineItemViewModel.FormatRelative(now, now.AddDays(-14)));
        // Future updatedAt clamps to "just now".
        Assert.Equal("just now", TimelineItemViewModel.FormatRelative(now, now.AddMinutes(5)));
    }

    [Fact]
    public void ToggleExpand_FlipsIsExpanded()
    {
        var (vm, _, _, _, _, _) = BuildVm();

        Assert.False(vm.IsExpanded);
        vm.ToggleExpandCommand.Execute(null);
        Assert.True(vm.IsExpanded);
        vm.ToggleExpandCommand.Execute(null);
        Assert.False(vm.IsExpanded);
    }

    [Fact]
    public void ReasonBadge_DerivesFromReason()
    {
        var (vm, _, _, _, _, _) = BuildVm(reason: NotificationReason.Review);
        Assert.Equal("REVIEW", vm.ReasonBadgeText);
        Assert.False(string.IsNullOrEmpty(vm.ReasonBadgeColor));
    }

    [Fact]
    public void DisplayUserLogin_FallsBackToOwnerWhenActorLoginNull()
    {
        // Cold-cache rows that have not been selected yet show the repo
        // owner as a stop-gap. Once a per-row backfill flips ActorLogin,
        // DisplayUserLogin tracks the actor.
        var (vm, _, _, _, _, _) = BuildVm();
        Assert.Null(vm.ActorLogin);
        Assert.Equal("octocat", vm.DisplayUserLogin);

        vm.ActorLogin = "coderabbitai[bot]";
        Assert.Equal("coderabbitai[bot]", vm.DisplayUserLogin);
    }

    [Fact]
    public async Task EnsureBodyLoaded_PopulatesActorLogin_FromLatestComment_AndPersists()
    {
        // The detail-pane fetch surfaces both the latest comment body AND
        // its author login. When ActorLogin starts null, the VM must adopt
        // the resolved login and persist it to both repositories so future
        // sessions / sibling rows show the real bot author rather than the
        // repo owner stop-gap.
        var repo = new FakeNotificationRepository();
        var evRepo = new FakeNotificationEventRepository();
        var api = new FakeApiClient();
        var clock = new FakeClock();

        var clockNow = clock.UtcNow;
        var t0 = clockNow.AddMinutes(-5);

        // Seed an event that is the latest observation of the thread so
        // EnsureBodyLoadedAsync's preferComment heuristic picks the
        // latest-comment branch (and thus a non-null author login).
        var ev = TimelineTestData.BuildEvent(
            eventId: 0,
            id: "primary:1",
            accountId: "primary",
            repo: "octocat/hello",
            title: "Add CONTRIBUTING.md",
            reason: NotificationReason.Mention,
            unread: true,
            updatedAt: t0,
            threadId: "1",
            webUrl: "https://github.com/octocat/hello/pull/1");
        await evRepo.TryAppendAsync(ev);

        // Mirror the notification side so SetActorLoginAsync has a row to
        // hit on the cache repo as well.
        var notif = TimelineTestData.Build(
            id: "primary:1",
            accountId: "primary",
            repo: "octocat/hello",
            title: "Add CONTRIBUTING.md",
            reason: NotificationReason.Mention,
            unread: true,
            updatedAt: t0,
            threadId: "1",
            webUrl: "https://github.com/octocat/hello/pull/1");
        await repo.UpsertAsync(notif, "{}", clockNow);

        api.GetLatestCommentDetailsOverride = _ => ("LGTM", "coderabbitai[bot]");

        // Use the just-assigned event id (FakeNotificationEventRepository
        // assigns an autoincrement). We need the event-backed VM constructor.
        var assigned = evRepo.Events.Single();

        var ctx = new TimelineItemContext(
            Repository: repo,
            EventRepository: evRepo,
            Api: api,
            Browser: new FakeBrowser(),
            Clipboard: new FakeClipboard(),
            Clock: clock,
            PatProvider: _ => Task.FromResult<string?>("pat"),
            OnMarkRead: null,
            Log: null);

        // Use a synthesized event with a Subject API URL so the body fetch
        // path doesn't need the recovery network probe.
        var withApiUrl = assigned with
        {
            Subject = new NotificationSubject(
                "PullRequest",
                assigned.Subject.Title,
                "https://api.github.com/repos/octocat/hello/pulls/1",
                assigned.Subject.WebUrl),
        };

        var vm = new TimelineItemViewModel(withApiUrl, ctx);
        Assert.Null(vm.ActorLogin);
        Assert.Equal("octocat", vm.DisplayUserLogin);

        await vm.EnsureBodyLoadedAsync();

        Assert.Equal("coderabbitai[bot]", vm.BodyAuthorLogin);
        Assert.Equal("coderabbitai[bot]", vm.ActorLogin);
        Assert.Equal("coderabbitai[bot]", vm.DisplayUserLogin);

        // Persisted to both stores.
        Assert.Equal("coderabbitai[bot]", evRepo.Events.Single().ActorLogin);
        Assert.Equal("coderabbitai[bot]", repo.Notifications.Single().ActorLogin);
    }
}
