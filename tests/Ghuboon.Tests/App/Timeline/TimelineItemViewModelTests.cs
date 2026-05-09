using System;
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
        Assert.Single(api.MarkedReadThreads);
        Assert.Equal("1", api.MarkedReadThreads[0]);
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
}
