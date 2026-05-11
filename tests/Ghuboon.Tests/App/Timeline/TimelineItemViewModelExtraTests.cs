using System;
using System.Linq;
using System.Threading.Tasks;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Timeline;

/// <summary>
/// Phase 15: extra coverage for <see cref="TimelineItemViewModel"/> read-state
/// transitions and command idempotence / coalescing.
/// </summary>
public class TimelineItemViewModelExtraTests
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
    public async Task MarkAsRead_AlreadyRead_DoesNotCallApi_AndDoesNotUpsert()
    {
        var (vm, repo, api, _, _, _) = BuildVm(unread: false);

        Assert.False(vm.Unread);
        await vm.MarkAsReadCommand.ExecuteAsync(null);

        Assert.False(vm.Unread);
        Assert.Empty(api.MarkedReadThreads);
        Assert.Equal(0, repo.UpsertCallCount);
        Assert.Equal(0, repo.SetReadStateCallCount);
    }

    [Fact]
    public async Task MarkAsRead_ApiFailure_DoesNotThrow_AndShowsFlashMessage()
    {
        var (vm, repo, api, _, _, _) = BuildVm(unread: true);
        api.MarkReadOverride = _ => new InvalidOperationException("transient");

        // Must not propagate; ADR-014 says read sync failures degrade gracefully.
        var ex = await Record.ExceptionAsync(() => vm.MarkAsReadCommand.ExecuteAsync(null));
        Assert.Null(ex);

        Assert.True(vm.Unread);
        Assert.NotNull(vm.FlashMessage);
        Assert.Contains("retry", vm.FlashMessage, StringComparison.OrdinalIgnoreCase);
        // No local DB write on failure: state stays in sync with the
        // (still-unread) cache.
        Assert.Equal(0, repo.UpsertCallCount);
        Assert.Equal(0, repo.SetReadStateCallCount);
    }

    [Fact]
    public async Task OpenInGitHub_WhenAlreadyRead_OpensBrowser_NoApiCall()
    {
        var (vm, _, api, browser, _, _) = BuildVm(unread: false);

        await vm.OpenInGitHubCommand.ExecuteAsync(null);

        Assert.Single(browser.OpenedUrls);
        Assert.Equal("https://github.com/octocat/hello/pull/1", browser.OpenedUrls[0]);
        Assert.Empty(api.MarkedReadThreads);
    }

    [Fact]
    public async Task MarkAsRead_ConcurrentExecutions_CoalesceToSingleApiCall()
    {
        // Issue #26: overlapping MarkAsRead invocations must collapse to a
        // single GitHub API call. The fix uses
        // [RelayCommand(AllowConcurrentExecutions = false)] so the second
        // ExecuteAsync no-ops while the first is still in flight; the second
        // call also sees Unread already cleared once the first completes.
        var (vm, _, api, _, _, _) = BuildVm(unread: true);

        var gate = new TaskCompletionSource();
        api.MarkReadGate = gate;

        var first = vm.MarkAsReadCommand.ExecuteAsync(null);
        var second = vm.MarkAsReadCommand.ExecuteAsync(null);

        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.False(vm.Unread);
        // Snapshot before asserting to avoid races with any background mutations.
        var threads = api.MarkedReadThreads.ToList();
        Assert.Single(threads);
    }
}
