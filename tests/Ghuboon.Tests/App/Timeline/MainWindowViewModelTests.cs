using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Timeline;

public class MainWindowViewModelTests
{
    private static (MainWindowViewModel vm, FakeSyncService sync, FakeClock clock) Build(
        ITimelineService? timeline = null,
        Func<Task<string?>>? accountId = null)
    {
        var settings = new StubAppSettingsService();
        var clock = new FakeClock();
        var sync = new FakeSyncService();
        timeline ??= new StubTimelineService();
        accountId ??= () => Task.FromResult<string?>("primary");
        var vm = new MainWindowViewModel(settings, timeline, sync, clock, accountId);
        return (vm, sync, clock);
    }

    [Fact]
    public void UnreadCount_LiveUpdates_OnItemMarkedRead()
    {
        var (vm, _, _) = Build();
        // Stub timeline starts with 5 items; 3 of them are unread.
        var initial = vm.UnreadCount;
        Assert.True(initial > 0);

        // Flip one to read and verify the aggregate ticks down.
        var firstUnread = vm.Timeline.Items.First(i => i.Unread);
        firstUnread.Unread = false;

        Assert.Equal(initial - 1, vm.UnreadCount);
    }

    [Fact]
    public void SyncFailed_PreservesItems_AndShowsErrorWithRetry()
    {
        var (vm, sync, _) = Build();
        var beforeCount = vm.Timeline.Items.Count;
        Assert.True(beforeCount > 0);

        // Simulate a Failed progress event.
        var failed = new SyncResult(false, 0, 0, 0, ErrorCategory.Network, "down", null);
        sync.RaiseProgress(new SyncProgressEvent("primary", SyncStage.Failed, failed));

        Assert.Equal(beforeCount, vm.Timeline.Items.Count); // not cleared
        Assert.False(vm.IsSyncing);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("Network", vm.ErrorMessage);
    }

    [Fact]
    public async Task RetrySyncCommand_TriggersSync()
    {
        var (vm, sync, _) = Build();

        await vm.RetrySyncCommand.ExecuteAsync(null);

        Assert.Single(sync.SyncCalls);
        Assert.Equal("primary", sync.SyncCalls[0]);
    }

    [Fact]
    public void RateLimitText_IsFormattedFromSyncProgress()
    {
        var (vm, sync, _) = Build();
        var rl = new RateLimitInfo(4500, new DateTimeOffset(2026, 5, 9, 14, 32, 0, TimeSpan.Zero));
        var result = new SyncResult(true, 0, 0, 0, null, null, rl);
        sync.RaiseProgress(new SyncProgressEvent("primary", SyncStage.Completed, result));

        Assert.NotNull(vm.RateLimitText);
        Assert.Contains("4500", vm.RateLimitText);
    }

    [Fact]
    public void LastSyncText_IsRefreshedByTimer()
    {
        var (vm, _, clock) = Build();

        // Simulate a successful sync at clock t0.
        var t0 = clock.UtcNow;
        var result = new SyncResult(true, 0, 0, 0, null, null, RateLimitInfo.Empty);
        // Use the public Progress raise path to set LastSuccessfulSyncAt.
        var sync = (FakeSyncService)typeof(MainWindowViewModelTests)
            .GetMethod(nameof(GetFakeSyncFromVm), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { vm })!;
        sync.RaiseProgress(new SyncProgressEvent("primary", SyncStage.Completed, result));

        Assert.NotNull(vm.LastSyncText);
        Assert.Contains("Last sync", vm.LastSyncText);

        // Advance the clock and call the refresh helper.
        clock.UtcNow = t0.AddMinutes(5);
        vm.RefreshLastSyncText();
        Assert.Contains("5m ago", vm.LastSyncText);
    }

    [Fact]
    public void FormatSinceNow_BucketsCorrectly()
    {
        var now = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("just now", MainWindowViewModel.FormatSinceNow(now, now));
        Assert.Equal("just now", MainWindowViewModel.FormatSinceNow(now, now.AddSeconds(-15)));
        Assert.Equal("45s ago", MainWindowViewModel.FormatSinceNow(now, now.AddSeconds(-45)));
        Assert.Equal("3m ago", MainWindowViewModel.FormatSinceNow(now, now.AddMinutes(-3)));
        Assert.Equal("4h ago", MainWindowViewModel.FormatSinceNow(now, now.AddHours(-4)));
        Assert.Equal("2d ago", MainWindowViewModel.FormatSinceNow(now, now.AddDays(-2)));
    }

    [Theory]
    [InlineData(ErrorCategory.Auth, "Token invalid. Update PAT in Settings.")]
    [InlineData(ErrorCategory.Network, "Network unavailable. Will retry.")]
    [InlineData(ErrorCategory.ApiCompatibility, "GitHub API responded unexpectedly.")]
    [InlineData(ErrorCategory.Database, "Local cache error. Logs have details.")]
    [InlineData(ErrorCategory.Unknown, "Sync failed.")]
    public void ErrorCategory_IsMappedToFriendlyMessage(ErrorCategory category, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.MapErrorMessage(category, "raw error"));
    }

    [Fact]
    public void DismissError_ClearsErrorMessage()
    {
        var (vm, sync, _) = Build();
        var failed = new SyncResult(false, 0, 0, 0, ErrorCategory.Auth, "bad", null);
        sync.RaiseProgress(new SyncProgressEvent("primary", SyncStage.Failed, failed));
        Assert.NotNull(vm.ErrorMessage);

        vm.DismissErrorCommand.Execute(null);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task SyncAsync_NoAccount_ShowsConfigurePatStatus_AndDoesNotCallSync()
    {
        var (vm, sync, _) = Build(accountId: () => Task.FromResult<string?>(null));

        await vm.SyncCommand.ExecuteAsync(null);

        Assert.Empty(sync.SyncCalls);
        Assert.Contains("Configure PAT", vm.StatusText);
    }

    [Fact]
    public void SyncStartingProgress_SetsIsSyncing_AndClearsError()
    {
        var (vm, sync, _) = Build();
        // Pre-set an error.
        sync.RaiseProgress(new SyncProgressEvent("primary", SyncStage.Failed,
            new SyncResult(false, 0, 0, 0, ErrorCategory.Network, "down", null)));
        Assert.NotNull(vm.ErrorMessage);

        sync.RaiseProgress(new SyncProgressEvent("primary", SyncStage.Starting, null));

        Assert.True(vm.IsSyncing);
        Assert.Null(vm.ErrorMessage);
    }

    // Reflection helper to retrieve the FakeSyncService field for the LastSyncText test.
    private static FakeSyncService GetFakeSyncFromVm(MainWindowViewModel vm)
    {
        var f = typeof(MainWindowViewModel).GetField("_syncService",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (FakeSyncService)f!.GetValue(vm)!;
    }
}
