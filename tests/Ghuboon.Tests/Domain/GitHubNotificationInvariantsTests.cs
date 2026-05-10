using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.Domain;

/// <summary>
/// Phase 2 hardening: covers the constructor invariants on
/// <see cref="GitHubNotification"/> introduced for issue #6.
/// </summary>
public class GitHubNotificationInvariantsTests
{
    private const string AccountId = "acct-1";
    private const string ThreadId = "thread-1";
    private const string ValidId = "acct-1:thread-1";
    private const string ValidRepo = "octocat/hello";

    private static readonly NotificationSubject Subject =
        new("PullRequest", "Sample title", null, null);

    private static readonly DateTimeOffset Updated =
        new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private static GitHubNotification Build(
        string id = ValidId,
        string accountId = AccountId,
        string threadId = ThreadId,
        string repositoryFullName = ValidRepo) =>
        new(
            Id: id,
            AccountId: accountId,
            ThreadId: threadId,
            RepositoryFullName: repositoryFullName,
            Subject: Subject,
            Reason: NotificationReason.Mention,
            Unread: true,
            UpdatedAt: Updated,
            LastReadAt: null);

    [Fact]
    public void Constructor_HappyPath_PopulatesAllFields()
    {
        var notif = Build();

        Assert.Equal(ValidId, notif.Id);
        Assert.Equal(AccountId, notif.AccountId);
        Assert.Equal(ThreadId, notif.ThreadId);
        Assert.Equal(ValidRepo, notif.RepositoryFullName);
        Assert.Equal(Subject, notif.Subject);
        Assert.Equal(NotificationReason.Mention, notif.Reason);
        Assert.True(notif.Unread);
        Assert.Equal(Updated, notif.UpdatedAt);
        Assert.Null(notif.LastReadAt);
    }

    [Fact]
    public void Constructor_NullId_Throws_ArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Build(id: null!));
        Assert.Equal("Id", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullAccountId_Throws_ArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Build(accountId: null!));
        Assert.Equal("AccountId", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullThreadId_Throws_ArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Build(threadId: null!));
        Assert.Equal("ThreadId", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullRepositoryFullName_Throws_ArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Build(repositoryFullName: null!));
        Assert.Equal("RepositoryFullName", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_BlankId_Throws_ArgumentException(string id)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(id: id));
        Assert.Equal("Id", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_BlankAccountId_Throws_ArgumentException(string accountId)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(accountId: accountId));
        Assert.Equal("AccountId", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_BlankThreadId_Throws_ArgumentException(string threadId)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(threadId: threadId));
        Assert.Equal("ThreadId", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_BlankRepositoryFullName_Throws_ArgumentException(string repo)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(repositoryFullName: repo));
        Assert.Equal("RepositoryFullName", ex.ParamName);
    }

    [Theory]
    [InlineData("octocat")]                  // no slash
    [InlineData("octocat/hello/extra")]      // too many slashes
    [InlineData("/hello")]                  // empty owner
    [InlineData("octocat/")]                 // empty name
    [InlineData("/")]                       // both empty
    [InlineData(" /hello")]                  // whitespace owner
    [InlineData("octocat/ ")]                // whitespace name
    public void Constructor_MalformedRepositoryFullName_Throws_ArgumentException(string repo)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(repositoryFullName: repo));
        Assert.Equal("RepositoryFullName", ex.ParamName);
    }

    [Fact]
    public void Constructor_IdNotMatchingAccountAndThread_Throws_ArgumentException()
    {
        // accountId="acct-1", threadId="thread-1" => expected Id "acct-1:thread-1"
        var ex = Assert.Throws<ArgumentException>(() =>
            Build(id: "acct-1:something-else"));
        Assert.Equal("Id", ex.ParamName);
    }

    [Fact]
    public void Constructor_IdMissingColon_Throws_ArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(id: "acct-1.thread-1"));
        Assert.Equal("Id", ex.ParamName);
    }

    [Fact]
    public void Constructor_IdSwappedAccountAndThread_Throws_ArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(id: "thread-1:acct-1"));
        Assert.Equal("Id", ex.ParamName);
    }

    [Fact]
    public void Record_With_Expression_PreservesInvariants_OnHappyPath()
    {
        var notif = Build();
        var copied = notif with { Unread = false };
        Assert.False(copied.Unread);
        Assert.Equal(ValidId, copied.Id);
    }
}
