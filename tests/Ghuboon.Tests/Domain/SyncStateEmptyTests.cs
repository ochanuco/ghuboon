using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.Domain;

/// <summary>
/// Phase 2 hardening: <see cref="SyncState.Empty"/> rejects null/empty/whitespace
/// account ids so callers can't accidentally produce a sentinel sync state with
/// no owning account.
/// </summary>
public class SyncStateEmptyTests
{
    [Fact]
    public void Empty_HappyPath_ProducesAllNullsExceptAccountId()
    {
        var state = SyncState.Empty("acct-1");

        Assert.Equal("acct-1", state.AccountId);
        Assert.Null(state.LastSyncAt);
        Assert.Null(state.LastSuccessfulSyncAt);
        Assert.Null(state.NotificationsEtag);
        Assert.Null(state.RateLimitRemaining);
        Assert.Null(state.RateLimitResetAt);
    }

    [Fact]
    public void Empty_NullAccountId_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => SyncState.Empty(null!));
        Assert.Equal("accountId", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   ")]
    public void Empty_BlankAccountId_Throws(string accountId)
    {
        var ex = Assert.Throws<ArgumentException>(() => SyncState.Empty(accountId));
        Assert.Equal("accountId", ex.ParamName);
    }
}
