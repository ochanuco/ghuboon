using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.Headless;

/// <summary>
/// Smoke E2E for <see cref="TimelineViewModel"/> running on the real
/// Avalonia dispatcher (booted via <see cref="HeadlessDispatcherFixture"/>).
/// The existing <c>TimelineViewModelTests</c> drive the VM inline because
/// <c>Avalonia.Application.Current</c> is null in process — they cover
/// the logic but not the <c>UIThread.InvokeAsync</c> marshalling chain
/// that produced the silently-dropped PropertyChanged regressions earlier
/// this cycle. This file pins that chain: if a future change starts
/// firing notifications off-thread, the dispatcher path will surface it
/// here instead of in production.
/// </summary>
[Trait("Category", "Headless")]
public class TimelineViewModelHeadlessSmokeTests
{
    [Fact]
    public Task ReloadAsync_PopulatesItems_ThroughRealDispatcher()
        => HeadlessDispatcherFixture.RunOnDispatcher(async () =>
        {
            // Sanity: the test body should now be on the Avalonia UI
            // thread. If it isn't, the headless boot didn't wire the
            // dispatcher and every later assertion is meaningless.
            Assert.True(Dispatcher.UIThread.CheckAccess(),
                "Test body must run on the Avalonia UI thread.");

            var fake = new FakeTimelineService();
            fake.Add(NotificationReason.Mention, "octocat/a", "row-a");
            fake.Add(NotificationReason.Review, "octocat/b", "row-b");
            fake.Add(NotificationReason.MyPr, "octocat/c", "row-c");

            var vm = new TimelineViewModel(fake);
            await vm.ReloadAsync();

            // ReloadAsync goes off-thread for the DB / VM-construct work
            // then marshals back onto UIThread.InvokeAsync for the bound
            // collection mutations. Items should be observable from here
            // (still UI thread).
            Assert.Equal(3, vm.Items.Count);
            Assert.Equal(new[] { "row-a", "row-b", "row-c" }.OrderBy(x => x),
                vm.Items.Select(i => i.Title).OrderBy(x => x));
        });

    /// <summary>
    /// Inline ITimelineService stub. Mirrors the private fake in
    /// <c>App.Timeline.TimelineViewModelTests</c> but is intentionally
    /// kept tiny — the headless smoke only needs Add + LoadAsync.
    /// </summary>
    private sealed class FakeTimelineService : ITimelineService
    {
        private readonly List<NotificationEvent> _events = new();
        private long _nextId = 1;

        public void Add(NotificationReason reason, string repo, string title,
            DateTimeOffset? updatedAt = null)
        {
            var threadId = Guid.NewGuid().ToString();
            var when = updatedAt ?? DateTimeOffset.UtcNow;
            _events.Add(new NotificationEvent(
                Id: _nextId++,
                AccountId: "primary",
                NotificationId: $"primary:{threadId}",
                ThreadId: threadId,
                RepositoryFullName: repo,
                Subject: new NotificationSubject("PullRequest", title, null, null),
                Reason: reason,
                SourceUpdatedAt: when,
                ObservedAt: when,
                Unread: true,
                LastReadAt: null,
                RawJson: "{}"));
        }

        public Task<IReadOnlyList<NotificationEvent>> LoadAsync(TimelineFilter filter, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NotificationEvent>>(_events);

        public Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositoryRef>>(Array.Empty<RepositoryRef>());

        public IReadOnlyList<NotificationEvent> GetPlaceholderItems() => Array.Empty<NotificationEvent>();
    }
}
