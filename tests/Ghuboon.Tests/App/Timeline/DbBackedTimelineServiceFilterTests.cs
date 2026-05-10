using System;
using System.Threading.Tasks;
using Ghuboon.App.Services;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Timeline;

/// <summary>
/// Phase 15: additional coverage for <see cref="DbBackedTimelineService"/>.
/// Focuses on filter combinations, search field coverage, whitespace handling,
/// and case-insensitivity invariants the existing tests do not exercise.
/// </summary>
public class DbBackedTimelineServiceFilterTests
{
    private static (DbBackedTimelineService svc, FakeNotificationRepository nrepo) Build()
    {
        var nrepo = new FakeNotificationRepository();
        var rrepo = new FakeRepositoryRepository();
        var arepo = new FakeAccountRepository();
        var svc = new DbBackedTimelineService(nrepo, rrepo, arepo, accountId: "primary");

        var t0 = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        nrepo.Notifications.Add(TimelineTestData.Build(
            "primary:a", "primary", "octocat/repo1", "Add CONTRIBUTING",
            NotificationReason.Review, true, t0.AddMinutes(-10), "PullRequest"));
        nrepo.Notifications.Add(TimelineTestData.Build(
            "primary:b", "primary", "octocat/repo2", "Mention me",
            NotificationReason.Mention, true, t0.AddMinutes(-2), "Issue"));
        nrepo.Notifications.Add(TimelineTestData.Build(
            "primary:c", "primary", "octocat/repo1", "Review the PR",
            NotificationReason.Review, false, t0.AddMinutes(-30), "PullRequest"));
        nrepo.Notifications.Add(TimelineTestData.Build(
            "primary:d", "primary", "ghuboon/playground", "My PR",
            NotificationReason.MyPr, true, t0.AddHours(-2), "PullRequest"));
        return (svc, nrepo);
    }

    [Fact]
    public async Task Load_CombinedFilters_TabAndRepoAndSearch_IntersectsAll()
    {
        var (svc, _) = Build();

        var filter = new TimelineFilter(TimelineTab.Review, "octocat/repo1", "review");
        var result = await svc.LoadAsync(filter);

        // Two Review rows on octocat/repo1 ("Add CONTRIBUTING" + "Review the PR").
        // "review" matches "Review the PR" via title and the Reason enum value
        // for both rows; the intersection yields both.
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal(NotificationReason.Review, r.Reason));
        Assert.All(result, r => Assert.Equal("octocat/repo1", r.RepositoryFullName));
    }

    [Fact]
    public async Task Load_CombinedFilters_NarrowsToSingleRow()
    {
        var (svc, _) = Build();

        // Tab=Review + repo=octocat/repo1 + search matching only the title
        // "Add CONTRIBUTING" keeps just that one row.
        var filter = new TimelineFilter(TimelineTab.Review, "octocat/repo1", "CONTRIBUTING");
        var result = await svc.LoadAsync(filter);

        Assert.Single(result);
        Assert.Equal("Add CONTRIBUTING", result[0].Subject.Title);
    }

    [Theory]
    [InlineData("octocat/repo2", 1)]   // matches RepositoryFullName
    [InlineData("Mention me", 1)]      // matches Subject.Title
    [InlineData("MyPr", 1)]            // matches Reason
    [InlineData("Issue", 1)]           // matches Subject.Type
    public async Task Load_Search_HitsEachField(string needle, int expected)
    {
        var (svc, _) = Build();
        var result = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, null, needle));
        Assert.Equal(expected, result.Count);
    }

    [Fact]
    public async Task Load_EmptySearch_ReturnsAll()
    {
        var (svc, _) = Build();

        var byNull = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, null, null));
        var byEmpty = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, null, string.Empty));

        Assert.Equal(byNull.Count, byEmpty.Count);
        Assert.Equal(4, byEmpty.Count);
    }

    [Fact]
    public async Task Load_WhitespaceSearch_BehavesLikeEmpty()
    {
        var (svc, _) = Build();

        var byEmpty = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, null, string.Empty));
        var byWs = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, null, "   \t  "));

        Assert.Equal(byEmpty.Count, byWs.Count);
    }

    [Theory]
    [InlineData("MENTION")]
    [InlineData("mention")]
    [InlineData("MeNtIoN")]
    public async Task Load_Search_IsOrdinalIgnoreCase(string needle)
    {
        var (svc, _) = Build();
        var result = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, null, needle));
        // Each variant must produce the same hits (the title "Mention me" and
        // the Mention reason value).
        Assert.NotEmpty(result);
        Assert.Contains(result, r => r.Subject.Title == "Mention me");
    }

    [Fact]
    public async Task Load_RepoFilter_IsOrdinalIgnoreCase()
    {
        var (svc, _) = Build();
        var result = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, "OCTOCAT/REPO1", null));
        // Two rows on octocat/repo1.
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("octocat/repo1", r.RepositoryFullName));
    }

    [Fact]
    public async Task Load_TrimsLeadingTrailingWhitespaceOnSearch()
    {
        var (svc, _) = Build();
        var result = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, null, "  ghuboon  "));
        Assert.Single(result);
        Assert.Equal("ghuboon/playground", result[0].RepositoryFullName);
    }
}
