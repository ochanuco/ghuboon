using Ghuboon.App.ViewModels.Settings;

namespace Ghuboon.Tests.App.Settings;

public class CacheSettingsViewModelTests
{
    [Fact]
    public async Task Clear_CallsDeleteOlderThan_WithCutoffAtOrAfterNow()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        var repo = new FakeNotificationRepository { RowsToReturn = 7 };
        var vm = new CacheSettingsViewModel(repo, fakeClock);

        await vm.ClearCacheCommand.ExecuteAsync(null);

        Assert.Single(repo.DeleteOlderThanCalls);
        Assert.True(repo.DeleteOlderThanCalls[0] >= fakeClock.GetUtcNow());
        Assert.Equal(7, vm.LastDeletedRowCount);
        Assert.Contains("7", vm.StatusMessage);
    }

    [Fact]
    public void Retention_IsThirtyDays()
    {
        var vm = new CacheSettingsViewModel(new FakeNotificationRepository());
        Assert.Equal("30 days", vm.CacheRetentionDisplay);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
