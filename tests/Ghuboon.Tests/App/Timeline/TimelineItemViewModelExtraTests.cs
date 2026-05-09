using System;
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
        // No local upsert on failure: state stays in sync with the (still-unread) cache.
        Assert.Equal(0, repo.UpsertCallCount);
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
    public async Task MarkAsRead_ConcurrentExecutions_CurrentlyDoNotCoalesce()
    {
        // Phase 15 finding: <see cref="TimelineItemViewModel"/> does NOT serialize
        // overlapping mark-read calls. Both pass the "if (!Unread)" check while
        // the first is still awaiting the API, so the API is called twice.
        // We pin down current behavior; if the VM later adds an in-flight guard
        // (or AllowConcurrentExecutions=false propagates), tighten this assertion.
        var (vm, _, api, _, _, _) = BuildVm(unread: true);

        var gate = new TaskCompletionSource();
        api.MarkReadGate = gate;

        var first = vm.MarkAsReadCommand.ExecuteAsync(null);
        var second = vm.MarkAsReadCommand.ExecuteAsync(null);

        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.False(vm.Unread);
        // TODO: collapse to a single API call if/when the VM adds idempotence
        // around in-flight mark-read commands. Today both call sites fire.
        Assert.Equal(2, api.MarkedReadThreads.Count);
    }
}
