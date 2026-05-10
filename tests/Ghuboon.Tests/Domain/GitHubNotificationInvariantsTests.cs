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

    /// <summary>
    /// Issue #35: identity-bearing properties (<c>Id</c>, <c>AccountId</c>,
    /// <c>ThreadId</c>, <c>RepositoryFullName</c>) are <c>private init</c> so a
    /// <c>with</c>-expression cannot replace one of them and bypass the
    /// constructor's <c>Id == "{AccountId}:{ThreadId}"</c> check. Verify a
    /// caller in this assembly cannot reach those setters via reflection-free
    /// access (the `with` would not compile externally; we exercise the runtime
    /// shape instead).
    /// </summary>
    [Fact]
    public void Identity_Properties_Are_Private_Init_To_Preserve_Invariants()
    {
        var idProperty = typeof(GitHubNotification).GetProperty(nameof(GitHubNotification.Id))!;
        var accountIdProperty = typeof(GitHubNotification).GetProperty(nameof(GitHubNotification.AccountId))!;
        var threadIdProperty = typeof(GitHubNotification).GetProperty(nameof(GitHubNotification.ThreadId))!;
        var repoProperty = typeof(GitHubNotification).GetProperty(nameof(GitHubNotification.RepositoryFullName))!;

        foreach (var prop in new[] { idProperty, accountIdProperty, threadIdProperty, repoProperty })
        {
            // The property must have a setter (init), but it must not be public
            // — otherwise a `with`-expression in another assembly could replace
            // it and skip constructor validation.
            var setter = prop.SetMethod;
            Assert.NotNull(setter);
            Assert.False(setter!.IsPublic, $"{prop.Name} setter must not be public to preserve identity invariants.");
        }

        // Mutable companions stay public-init.
        var unreadSetter = typeof(GitHubNotification).GetProperty(nameof(GitHubNotification.Unread))!.SetMethod;
        Assert.NotNull(unreadSetter);
        Assert.True(unreadSetter!.IsPublic, "Unread setter remains public-init for callers that produce derived records.");
    }
}
