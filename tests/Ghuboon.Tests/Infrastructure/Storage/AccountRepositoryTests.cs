using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

public class AccountRepositoryTests
{
    [Fact]
    public async Task Upsert_then_get_by_id_roundtrips_account()
    {
        await using var temp = new TempDatabase(seedAccounts: false);
        var repo = new AccountRepository(temp.Factory);

        var account = new Account(
            Id: "acct-1",
            Host: "github.com",
            Login: "octocat",
            CredentialKey: "ghuboon.acct-1.pat",
            CreatedAt: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            LastValidatedAt: new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero));

        await repo.UpsertAsync(account);

        var read = await repo.GetByIdAsync("acct-1");

        Assert.NotNull(read);
        Assert.Equal(account.Id, read!.Id);
        Assert.Equal(account.Host, read.Host);
        Assert.Equal(account.Login, read.Login);
        Assert.Equal(account.CredentialKey, read.CredentialKey);
        Assert.Equal(account.CreatedAt, read.CreatedAt);
        Assert.Equal(account.LastValidatedAt, read.LastValidatedAt);
    }

    [Fact]
    public async Task Upsert_replaces_existing_row_with_same_id()
    {
        await using var temp = new TempDatabase(seedAccounts: false);
        var repo = new AccountRepository(temp.Factory);

        var initial = new Account(
            "acct-1", "github.com", "octocat", "ghuboon.acct-1.pat",
            DateTimeOffset.UtcNow, null);
        await repo.UpsertAsync(initial);

        var updated = initial with
        {
            Login = "monalisa",
            LastValidatedAt = new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.Zero),
        };
        await repo.UpsertAsync(updated);

        var read = await repo.GetByIdAsync("acct-1");
        Assert.NotNull(read);
        Assert.Equal("monalisa", read!.Login);
        Assert.Equal(updated.LastValidatedAt, read.LastValidatedAt);

        var all = await repo.ListAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task List_returns_all_accounts_in_creation_order()
    {
        await using var temp = new TempDatabase(seedAccounts: false);
        var repo = new AccountRepository(temp.Factory);

        var a = new Account("a", "github.com", "a", "ka",
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), null);
        var b = new Account("b", "github.com", "b", "kb",
            new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero), null);

        await repo.UpsertAsync(b);
        await repo.UpsertAsync(a);

        var all = await repo.ListAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal("a", all[0].Id);
        Assert.Equal("b", all[1].Id);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_when_missing()
    {
        await using var temp = new TempDatabase(seedAccounts: false);
        var repo = new AccountRepository(temp.Factory);

        var read = await repo.GetByIdAsync("does-not-exist");
        Assert.Null(read);
    }

    // Issue #12: DeriveApiBaseUrl should normalize input (trim, ensure scheme,
    // parse Uri) and use uri.Host case-insensitively to detect github.com.
    [Theory]
    [InlineData("github.com", "https://api.github.com")]
    [InlineData("https://github.com", "https://api.github.com")]
    [InlineData("https://github.com/", "https://api.github.com")]
    [InlineData("https://github.com/api/v3", "https://api.github.com")]
    [InlineData("GitHub.COM", "https://api.github.com")]
    [InlineData("  github.com  ", "https://api.github.com")]
    [InlineData("ghe.example.com", "https://ghe.example.com/api/v3")]
    [InlineData("https://ghe.example.com", "https://ghe.example.com/api/v3")]
    [InlineData("https://ghe.example.com/", "https://ghe.example.com/api/v3")]
    [InlineData("https://ghe.example.com:8443/", "https://ghe.example.com:8443/api/v3")]
    [InlineData("http://ghe.internal", "http://ghe.internal/api/v3")]
    [InlineData("", "https://api.github.com")]
    [InlineData("   ", "https://api.github.com")]
    [InlineData(null, "https://api.github.com")]
    public void DeriveApiBaseUrl_normalizes_input(string? host, string expected)
    {
        Assert.Equal(expected, AccountRepository.DeriveApiBaseUrl(host));
    }
}
