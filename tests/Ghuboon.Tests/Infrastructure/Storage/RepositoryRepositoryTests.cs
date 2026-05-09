using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

public class RepositoryRepositoryTests
{
    [Fact]
    public async Task Upsert_then_get_by_full_name_roundtrips()
    {
        await using var temp = new TempDatabase();
        var repo = new RepositoryRepository(temp.Factory);

        var repository = new RepositoryRef(
            Id: "repo-1",
            AccountId: "acct-1",
            FullName: "ochanuco/ghuboon",
            Owner: "ochanuco",
            Name: "ghuboon",
            HtmlUrl: "https://github.com/ochanuco/ghuboon");

        await repo.UpsertAsync(repository);

        var read = await repo.GetByFullNameAsync("acct-1", "ochanuco/ghuboon");
        Assert.NotNull(read);
        Assert.Equal(repository, read);
    }

    [Fact]
    public async Task ListByAccount_filters_by_account()
    {
        await using var temp = new TempDatabase();
        var repo = new RepositoryRepository(temp.Factory);

        await repo.UpsertAsync(new RepositoryRef("r1", "a1", "a/repo1", "a", "repo1", ""));
        await repo.UpsertAsync(new RepositoryRef("r2", "a1", "a/repo2", "a", "repo2", ""));
        await repo.UpsertAsync(new RepositoryRef("r3", "a2", "b/repo1", "b", "repo1", ""));

        var listed = await repo.ListByAccountAsync("a1");

        Assert.Equal(2, listed.Count);
        Assert.All(listed, r => Assert.Equal("a1", r.AccountId));
    }

    [Fact]
    public async Task GetByFullName_returns_null_when_missing()
    {
        await using var temp = new TempDatabase();
        var repo = new RepositoryRepository(temp.Factory);

        Assert.Null(await repo.GetByFullNameAsync("acct-1", "nope/nope"));
    }
}
