using System;
using System.Threading.Tasks;
using Ghuboon.App.ViewModels.Settings;

namespace Ghuboon.Tests.App.Settings;

/// <summary>
/// Phase 15: edge cases around <see cref="CacheSettingsViewModel"/> reporting.
/// </summary>
public class CacheSettingsViewModelExtraTests
{
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public async Task Clear_ReportsSingularMessage_WhenOneRowDeleted()
    {
        var repo = new FakeNotificationRepository { RowsToReturn = 1 };
        var vm = new CacheSettingsViewModel(repo,
            new FixedClock(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero)));

        await vm.ClearCacheCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.LastDeletedRowCount);
        Assert.Equal("Cleared 1 cached notification.", vm.StatusMessage);
    }

    [Fact]
    public async Task Clear_ReportsZero_WhenNothingToClear()
    {
        var repo = new FakeNotificationRepository { RowsToReturn = 0 };
        var vm = new CacheSettingsViewModel(repo,
            new FixedClock(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero)));

        await vm.ClearCacheCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.LastDeletedRowCount);
        Assert.Contains("0", vm.StatusMessage);
    }
}
