using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

public class AppSettingsRepositoryTests
{
    [Fact]
    public async Task Set_then_get_returns_value()
    {
        await using var temp = new TempDatabase();
        var repo = new AppSettingsRepository(temp.Factory);

        await repo.SetAsync("ui.last_tab", "All");

        Assert.Equal("All", await repo.GetAsync("ui.last_tab"));
    }

    [Fact]
    public async Task Set_overwrites_existing_value()
    {
        await using var temp = new TempDatabase();
        var repo = new AppSettingsRepository(temp.Factory);

        await repo.SetAsync("ui.last_tab", "All");
        await repo.SetAsync("ui.last_tab", "Mention");

        Assert.Equal("Mention", await repo.GetAsync("ui.last_tab"));
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_key()
    {
        await using var temp = new TempDatabase();
        var repo = new AppSettingsRepository(temp.Factory);

        Assert.Null(await repo.GetAsync("nope"));
    }
}
