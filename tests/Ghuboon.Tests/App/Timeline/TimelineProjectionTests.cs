using System;
using System.Collections.Generic;
using System.Linq;
using Ghuboon.App.Services;
using Ghuboon.Core.Domain;
using Xunit;

namespace Ghuboon.Tests.App.Timeline;

/// <summary>
/// Pure-logic tests for <see cref="TimelineProjection"/>. These cover the
/// behaviour that was previously embedded in
/// <c>TimelineViewModel.ApplyCurrentFilter</c> — filtering, Comment vs
/// non-Comment dedup, and the thread-anchor ordering — without spinning
/// up a real <c>TimelineItemViewModel</c>.
/// </summary>
public class TimelineProjectionTests
{
    private sealed record Row(
        string Id,
        string NotificationId,
        string? LatestCommentApiUrl,
        NotificationEventKind EventKind,
        NotificationReason Reason,
        DateTimeOffset UpdatedAt,
        string RepositoryFullName = "octocat/repo",
        string Title = "row",
        string SubjectType = "PullRequest",
        bool IsBookmarked = false
    ) : ITimelineProjectionItem;

    private static readonly DateTimeOffset T0 =
        new(2026, 5, 16, 12, 0, 0, TimeSpan.Zero);

    private static TimelineFilter AllRepos(
        TimelineTab tab = TimelineTab.All,
        string? search = null) =>
        new(tab, RepositoryFullNames: null, SearchText: search);

    [Fact]
    public void Build_FiltersByTabReason()
    {
        var items = new List<Row>
        {
            new("a", "n1", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0),
            new("b", "n2", null, NotificationEventKind.Issue, NotificationReason.Mention, T0.AddMinutes(1)),
            new("c", "n3", null, NotificationEventKind.PullRequest, NotificationReason.MyPr, T0.AddMinutes(2)),
        };

        var result = TimelineProjection.Build(items, AllRepos(TimelineTab.Mention));

        Assert.Single(result);
        Assert.Equal("b", result[0].Id);
    }

    [Fact]
    public void Build_MyPrsTab_RequiresPullRequestKind()
    {
        // MyPr reason but kind=Issue must be excluded from MyPrs tab even
        // though the reason check would pass.
        var items = new List<Row>
        {
            new("a", "n1", null, NotificationEventKind.PullRequest, NotificationReason.MyPr, T0),
            new("b", "n2", null, NotificationEventKind.Issue, NotificationReason.MyPr, T0.AddMinutes(1)),
        };

        var result = TimelineProjection.Build(items, AllRepos(TimelineTab.MyPrs));

        Assert.Single(result);
        Assert.Equal("a", result[0].Id);
    }

    [Fact]
    public void Build_BookmarksTab_RequiresIsBookmarked()
    {
        var items = new List<Row>
        {
            new("a", "n1", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0, IsBookmarked: true),
            new("b", "n2", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0.AddMinutes(1)),
        };

        var result = TimelineProjection.Build(items, AllRepos(TimelineTab.Bookmarks));

        Assert.Single(result);
        Assert.Equal("a", result[0].Id);
    }

    [Fact]
    public void Build_RepositoryFilter_IsCaseInsensitive()
    {
        var items = new List<Row>
        {
            new("a", "n1", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0, RepositoryFullName: "OctoCat/Repo1"),
            new("b", "n2", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0.AddMinutes(1), RepositoryFullName: "octocat/other"),
        };
        var filter = new TimelineFilter(
            TimelineTab.All,
            new HashSet<string>(StringComparer.Ordinal) { "octocat/repo1" },
            SearchText: null);

        var result = TimelineProjection.Build(items, filter);

        Assert.Single(result);
        Assert.Equal("a", result[0].Id);
    }

    [Fact]
    public void Build_SearchText_MatchesTitleRepoReasonSubjectType()
    {
        var items = new List<Row>
        {
            new("title", "n1", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0, Title: "needle in title"),
            new("repo", "n2", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0.AddMinutes(1), RepositoryFullName: "ghuboon/needle"),
            new("subj", "n3", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0.AddMinutes(2), SubjectType: "NeedlePullRequest"),
            new("none", "n4", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0.AddMinutes(3), Title: "haystack"),
        };

        var result = TimelineProjection.Build(items, AllRepos(search: "  needle  "));
        var ids = result.Select(r => r.Id).ToList();

        Assert.Equal(3, result.Count);
        Assert.Contains("title", ids);
        Assert.Contains("repo", ids);
        Assert.Contains("subj", ids);
    }

    [Fact]
    public void Build_DedupsNonComment_KeepsLatestPerThread()
    {
        // Three PR-kind rows for the same NotificationId: only the latest
        // observation survives.
        var items = new List<Row>
        {
            new("first", "thread1", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0),
            new("middle", "thread1", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0.AddMinutes(5)),
            new("latest", "thread1", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0.AddMinutes(10)),
        };

        var result = TimelineProjection.Build(items, AllRepos());

        Assert.Single(result);
        Assert.Equal("latest", result[0].Id);
    }

    [Fact]
    public void Build_DedupsComment_ByNotificationIdAndCommentUrl()
    {
        // Two events with the same comment URL collapse; a different
        // comment URL on the same thread survives.
        var items = new List<Row>
        {
            new("c1-a", "thread1", "https://api.github.com/comments/1", NotificationEventKind.Comment, NotificationReason.Review, T0.AddMinutes(1)),
            new("c1-b", "thread1", "https://api.github.com/comments/1", NotificationEventKind.Comment, NotificationReason.Review, T0.AddMinutes(2)),
            new("c2", "thread1", "https://api.github.com/comments/2", NotificationEventKind.Comment, NotificationReason.Review, T0.AddMinutes(3)),
        };

        var result = TimelineProjection.Build(items, AllRepos());
        var ids = result.Select(r => r.Id).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains("c1-b", ids); // latest wins for the duplicate pair
        Assert.Contains("c2", ids);
    }

    [Fact]
    public void Build_OrdersThread_ParentBeforeComments_AndAnchorsBySendingThreadsByEarliestEvent()
    {
        // Thread A: PR observed at T0+30 (later), Comment observed at T0+5
        //   -> thread anchor should be T0+5 so cross-thread chronology
        //      uses the earliest event, then within-thread PR comes first.
        // Thread B: a standalone PR at T0+20.
        // Expected output order:
        //   1. Thread A PR (anchor=T0+5, KindPriority=0)
        //   2. Thread A Comment (anchor=T0+5, KindPriority=1)
        //   3. Thread B PR (anchor=T0+20)
        var items = new List<Row>
        {
            new("A-comment", "A", "https://api.github.com/comments/9", NotificationEventKind.Comment, NotificationReason.Review, T0.AddMinutes(5)),
            new("A-pr", "A", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0.AddMinutes(30)),
            new("B-pr", "B", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0.AddMinutes(20)),
        };

        var result = TimelineProjection.Build(items, AllRepos());
        var ids = result.Select(r => r.Id).ToList();

        Assert.Equal(new[] { "A-pr", "A-comment", "B-pr" }, ids);
    }

    [Fact]
    public void Build_EmptyNotificationId_IsNotDeduplicated()
    {
        // Rows without a NotificationId aren't grouped together and must
        // all survive.
        var items = new List<Row>
        {
            new("a", "", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0),
            new("b", "", null, NotificationEventKind.PullRequest, NotificationReason.Review, T0.AddMinutes(1)),
        };

        var result = TimelineProjection.Build(items, AllRepos());

        Assert.Equal(2, result.Count);
    }
}
