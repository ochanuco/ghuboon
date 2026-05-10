using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.Domain;

/// <summary>
/// Constructor invariants on <see cref="NotificationEvent"/>. Mirrors the
/// <see cref="GitHubNotificationInvariantsTests"/> shape so the two domain
/// types stay aligned: same identity rules, same private-init protections.
/// </summary>
public class NotificationEventInvariantsTests
{
    private const string AccountId = "acct-1";
    private const string ThreadId = "thread-1";
    private const string ValidNotificationId = "acct-1:thread-1";
    private const string ValidRepo = "octocat/hello";

    private static readonly NotificationSubject Subject =
        new("PullRequest", "Sample title", null, null);

    private static readonly DateTimeOffset Source =
        new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Observed =
        new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

    private static NotificationEvent Build(
        long id = 0,
        string accountId = AccountId,
        string notificationId = ValidNotificationId,
        string threadId = ThreadId,
        string repositoryFullName = ValidRepo,
        string rawJson = "{}") =>
        new(
            Id: id,
            AccountId: accountId,
            NotificationId: notificationId,
            ThreadId: threadId,
            RepositoryFullName: repositoryFullName,
            Subject: Subject,
            Reason: NotificationReason.Mention,
            SourceUpdatedAt: Source,
            ObservedAt: Observed,
            Unread: true,
            LastReadAt: null,
            RawJson: rawJson);

    [Fact]
    public void Constructor_HappyPath_PopulatesAllFields()
    {
        var ev = Build(id: 42);

        Assert.Equal(42, ev.Id);
        Assert.Equal(AccountId, ev.AccountId);
        Assert.Equal(ValidNotificationId, ev.NotificationId);
        Assert.Equal(ThreadId, ev.ThreadId);
        Assert.Equal(ValidRepo, ev.RepositoryFullName);
        Assert.Equal(Subject, ev.Subject);
        Assert.Equal(NotificationReason.Mention, ev.Reason);
        Assert.Equal(Source, ev.SourceUpdatedAt);
        Assert.Equal(Observed, ev.ObservedAt);
        Assert.True(ev.Unread);
        Assert.Null(ev.LastReadAt);
        Assert.Equal("{}", ev.RawJson);
    }

    [Fact]
    public void Constructor_NullAccountId_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Build(accountId: null!));
        Assert.Equal("AccountId", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullNotificationId_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Build(notificationId: null!));
        Assert.Equal("NotificationId", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullThreadId_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Build(threadId: null!));
        Assert.Equal("ThreadId", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullRepositoryFullName_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Build(repositoryFullName: null!));
        Assert.Equal("RepositoryFullName", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullRawJson_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Build(rawJson: null!));
        Assert.Equal("RawJson", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_BlankAccountId_Throws(string accountId)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(accountId: accountId));
        Assert.Equal("AccountId", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_BlankNotificationId_Throws(string notificationId)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(notificationId: notificationId));
        Assert.Equal("NotificationId", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_BlankThreadId_Throws(string threadId)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(threadId: threadId));
        Assert.Equal("ThreadId", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_BlankRepositoryFullName_Throws(string repo)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(repositoryFullName: repo));
        Assert.Equal("RepositoryFullName", ex.ParamName);
    }

    [Theory]
    [InlineData("octocat")]                  // no slash
    [InlineData("octocat/hello/extra")]      // too many slashes
    [InlineData("/hello")]                   // empty owner
    [InlineData("octocat/")]                 // empty name
    [InlineData("/")]                        // both empty
    [InlineData(" /hello")]                  // whitespace owner
    [InlineData("octocat/ ")]                // whitespace name
    public void Constructor_MalformedRepositoryFullName_Throws(string repo)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(repositoryFullName: repo));
        Assert.Equal("RepositoryFullName", ex.ParamName);
    }

    [Fact]
    public void Constructor_NotificationIdNotMatchingAccountAndThread_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Build(notificationId: "acct-1:something-else"));
        Assert.Equal("NotificationId", ex.ParamName);
    }

    [Fact]
    public void Record_With_Expression_PreservesIdentityInvariants()
    {
        var ev = Build();
        var copied = ev with { Unread = false };
        Assert.False(copied.Unread);
        Assert.Equal(ValidNotificationId, copied.NotificationId);
        Assert.Equal(AccountId, copied.AccountId);
        Assert.Equal(ThreadId, copied.ThreadId);
    }

    [Fact]
    public void Identity_Properties_Are_Private_Init_To_Preserve_Invariants()
    {
        // Mirrors GitHubNotification: identity-bearing fields use private
        // init so external `with` expressions cannot replace them and skip
        // the constructor's "NotificationId == {AccountId}:{ThreadId}"
        // invariant.
        var t = typeof(NotificationEvent);
        foreach (var name in new[] { "Id", "AccountId", "NotificationId", "ThreadId", "RepositoryFullName" })
        {
            var setter = t.GetProperty(name)!.SetMethod;
            Assert.NotNull(setter);
            Assert.False(setter!.IsPublic, $"{name} setter must not be public to preserve identity invariants.");
        }

        // Mutable companions remain public-init for derived-record use.
        foreach (var name in new[] { "Unread", "Subject", "Reason", "SourceUpdatedAt", "ObservedAt", "LastReadAt", "RawJson" })
        {
            var setter = t.GetProperty(name)!.SetMethod;
            Assert.NotNull(setter);
            Assert.True(setter!.IsPublic, $"{name} setter remains public-init.");
        }
    }
}
