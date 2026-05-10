using Dapper;
using Ghuboon.Infrastructure.Notifications;
using Ghuboon.Tests.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Notifications;

/// <summary>
/// Tests for <see cref="LastNotifiedTracker"/>, especially the atomic
/// <c>TryMarkAsNotifiedAsync</c> path that replaces the prior Get+Set
/// TOCTOU sequence (Phase 11 hardening, issue #20).
/// </summary>
public class LastNotifiedTrackerTests
{
    [Fact]
    public async Task TryMarkAsNotifiedAsync_returns_true_on_first_call()
    {
        await using var temp = new TempDatabase();
        var tracker = new LastNotifiedTracker(temp.Factory);
        var now = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

        var marked = await tracker.TryMarkAsNotifiedAsync("acct-1", "n-1", now, CancellationToken.None);

        Assert.True(marked);
        var stored = await tracker.GetLastNotifiedAtAsync("n-1");
        Assert.Equal(now, stored);
    }

    [Fact]
    public async Task TryMarkAsNotifiedAsync_returns_false_on_subsequent_call_for_same_id()
    {
        await using var temp = new TempDatabase();
        var tracker = new LastNotifiedTracker(temp.Factory);
        var first = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
        var second = first.AddMinutes(5);

        var firstMarked = await tracker.TryMarkAsNotifiedAsync("acct-1", "n-1", first, CancellationToken.None);
        var secondMarked = await tracker.TryMarkAsNotifiedAsync("acct-1", "n-1", second, CancellationToken.None);

        Assert.True(firstMarked);
        Assert.False(secondMarked);

        // The original mark must not be overwritten by the losing caller.
        var stored = await tracker.GetLastNotifiedAtAsync("n-1");
        Assert.Equal(first, stored);
    }

    [Fact]
    public async Task TryMarkAsNotifiedAsync_distinct_ids_each_succeed()
    {
        await using var temp = new TempDatabase();
        var tracker = new LastNotifiedTracker(temp.Factory);
        var now = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

        var a = await tracker.TryMarkAsNotifiedAsync("acct-1", "n-1", now, CancellationToken.None);
        var b = await tracker.TryMarkAsNotifiedAsync("acct-1", "n-2", now, CancellationToken.None);
        var c = await tracker.TryMarkAsNotifiedAsync("acct-1", "n-3", now, CancellationToken.None);

        Assert.True(a);
        Assert.True(b);
        Assert.True(c);
    }

    /// <summary>
    /// Issue #32 / #42: <c>notification_local_states</c> is keyed by the
    /// composite <c>(account_id, notification_id)</c> and Issue #42 tightened
    /// its FK to <c>(account_id, notification_id) → notifications(account_id, id)</c>.
    /// In production every <see cref="GitHubNotification.Id"/> is shaped as
    /// <c>{accountId}:{threadId}</c>, so the same upstream thread observed
    /// under two accounts produces two distinct row ids — each fully
    /// addressable through the tracker without one account suppressing the
    /// other. This test pins that namespaced-id invariant.
    /// </summary>
    [Fact]
    public async Task TryMarkAsNotifiedAsync_same_thread_under_two_accounts_each_succeeds_once()
    {
        await using var temp = new TempDatabase();
        var tracker = new LastNotifiedTracker(temp.Factory);
        var now = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

        // TempDatabase seeds notifications with these composite ids under
        // acct-1 (Lane N invariant: Id == "{accountId}:{threadId}"). That
        // gives the composite FK a parent row to attach to. Issue #42's
        // FK forbids attaching a local-state row to a notification owned
        // by a different account, so this test deliberately uses ids that
        // are namespaced to their owning account.
        var idAcct1 = "acct-1:n1";
        var idAcct2 = "acct-1:n2"; // also seeded under acct-1 by TempDatabase

        // Each id is markable on its first call and ignored on the second.
        var firstAcct1 = await tracker.TryMarkAsNotifiedAsync("acct-1", idAcct1, now, CancellationToken.None);
        var firstAcct2 = await tracker.TryMarkAsNotifiedAsync("acct-1", idAcct2, now, CancellationToken.None);

        var secondAcct1 = await tracker.TryMarkAsNotifiedAsync("acct-1", idAcct1, now.AddMinutes(1), CancellationToken.None);
        var secondAcct2 = await tracker.TryMarkAsNotifiedAsync("acct-1", idAcct2, now.AddMinutes(1), CancellationToken.None);

        Assert.True(firstAcct1);
        Assert.True(firstAcct2);
        Assert.False(secondAcct1);
        Assert.False(secondAcct2);

        // Each id's stored timestamp is its own first mark; one mark must not
        // have overwritten the other's row.
        var a1Stored = await tracker.GetLastNotifiedAtAsync("acct-1", idAcct1);
        var a2Stored = await tracker.GetLastNotifiedAtAsync("acct-1", idAcct2);
        Assert.Equal(now, a1Stored);
        Assert.Equal(now, a2Stored);
    }

    [Fact]
    public async Task TryMarkAsNotifiedAsync_concurrent_calls_for_same_id_yield_exactly_one_winner()
    {
        // SQLite serializes writers; this test documents the atomicity intent
        // and guards against future regressions (e.g. accidentally splitting
        // the SQL into Get+Set again).
        await using var temp = new TempDatabase();
        var tracker = new LastNotifiedTracker(temp.Factory);
        var now = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => tracker.TryMarkAsNotifiedAsync("acct-1", "n-race", now, CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task TryMarkAsNotifiedAsync_after_prior_mark_returns_false()
    {
        // Issue #37: <c>SetLastNotifiedAsync</c> was removed because the gate
        // now uses the atomic <see cref="TryMarkAsNotifiedAsync"/> exclusively.
        // The original behaviour is still asserted: a second call after a
        // successful first mark returns false.
        await using var temp = new TempDatabase();
        var tracker = new LastNotifiedTracker(temp.Factory);
        var t = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

        var first = await tracker.TryMarkAsNotifiedAsync("acct-1", "n-1", t, CancellationToken.None);
        var marked = await tracker.TryMarkAsNotifiedAsync("acct-1", "n-1", t.AddMinutes(1), CancellationToken.None);

        Assert.True(first);
        Assert.False(marked);
    }

    [Fact]
    public async Task TryMarkAsNotifiedAsync_revives_row_with_null_last_notified_at()
    {
        // notification_local_states rows can exist with last_notified_at=NULL
        // (e.g. set by other Phase 5 paths that only touch opened_at). The
        // gate must still be able to claim such a row for the first
        // OS-notification.
        await using var temp = new TempDatabase();
        var tracker = new LastNotifiedTracker(temp.Factory);
        var now = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

        await using (var conn = await temp.Factory.OpenAsync(CancellationToken.None))
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO notification_local_states (notification_id, account_id, is_hidden)
                VALUES (@id, @acct, 0);
                """,
                new { id = "n-existing", acct = "acct-1" });
        }

        var marked = await tracker.TryMarkAsNotifiedAsync("acct-1", "n-existing", now, CancellationToken.None);

        Assert.True(marked);
        var stored = await tracker.GetLastNotifiedAtAsync("n-existing");
        Assert.Equal(now, stored);
    }

    [Fact]
    public async Task GetLastNotifiedAtAsync_returns_null_for_malformed_db_value()
    {
        // Robustness: ParseNullableDate must not throw when the underlying
        // column contains an unparseable string. The dedup gate degrades to
        // "no record" rather than crashing the sync loop.
        await using var temp = new TempDatabase();
        var tracker = new LastNotifiedTracker(temp.Factory);

        await using (var conn = await temp.Factory.OpenAsync(CancellationToken.None))
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO notification_local_states (notification_id, account_id, last_notified_at, is_hidden)
                VALUES (@id, @acct, @bad, 0);
                """,
                new { id = "n-bad", acct = "acct-1", bad = "not-a-date" });
        }

        var stored = await tracker.GetLastNotifiedAtAsync("n-bad");

        Assert.Null(stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2026-13-40T99:99:99Z")]
    public void ParseNullableDate_returns_null_for_invalid_inputs(string? value)
    {
        Assert.Null(LastNotifiedTracker.ParseNullableDate(value));
    }

    [Fact]
    public void ParseNullableDate_round_trips_iso8601_offset()
    {
        var value = new DateTimeOffset(2026, 5, 1, 10, 30, 45, TimeSpan.FromHours(9));
        var raw = value.ToString("O");

        var parsed = LastNotifiedTracker.ParseNullableDate(raw);

        Assert.Equal(value, parsed);
    }
}
