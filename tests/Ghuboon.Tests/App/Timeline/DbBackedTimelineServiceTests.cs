using System;
using System.Linq;
using System.Threading.Tasks;
using Ghuboon.App.Services;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Timeline;

public class DbBackedTimelineServiceTests
{
    private static (DbBackedTimelineService svc, FakeNotificationRepository nrepo, FakeRepositoryRepository rrepo) Build()
    {
        var nrepo = new FakeNotificationRepository();
        var rrepo = new FakeRepositoryRepository();
        var arepo = new FakeAccountRepository();
        var svc = new DbBackedTimelineService(nrepo, rrepo, arepo, accountId: "primary");

        var t0 = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        nrepo.Notifications.Add(TimelineTestData.Build("primary:a", "primary", "octocat/repo1", "Add CONTRIBUTING", NotificationReason.Review, true, t0.AddMinutes(-10), "PullRequest"));
        nrepo.Notifications.Add(TimelineTestData.Build("primary:b", "primary", "octocat/repo2", "Mention me", NotificationReason.Mention, true, t0.AddMinutes(-2), "Issue"));
        nrepo.Notifications.Add(TimelineTestData.Build("primary:c", "primary", "octocat/repo1", "TeamMention!", NotificationReason.TeamMention, false, t0.AddMinutes(-30), "PullRequest"));
        nrepo.Notifications.Add(TimelineTestData.Build("primary:d", "primary", "ghuboon/playground", "My PR", NotificationReason.MyPr, true, t0.AddHours(-2), "PullRequest"));
        nrepo.Notifications.Add(TimelineTestData.Build("primary:e", "primary", "watcher/repo", "Watching", NotificationReason.Watching, false, t0.AddDays(-1), "Issue"));
        // Other-account row should be ignored.
        nrepo.Notifications.Add(TimelineTestData.Build("other:x", "other", "x/y", "no", NotificationReason.Mention, true, t0));

        return (svc, nrepo, rrepo);
    }

    [Fact]
    public async Task Load_AppliesTabFilter_Mention_IncludesTeamMention()
    {
        var (svc, _, _) = Build();
        var result = await svc.LoadAsync(new TimelineFilter(TimelineTab.Mention, null, null));

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Contains(r.Reason, new[] { NotificationReason.Mention, NotificationReason.TeamMention }));
    }

    [Fact]
    public async Task Load_AppliesTabFilter_Review()
    {
        var (svc, _, _) = Build();
        var result = await svc.LoadAsync(new TimelineFilter(TimelineTab.Review, null, null));

        Assert.Single(result);
        Assert.Equal(NotificationReason.Review, result[0].Reason);
    }

    [Fact]
    public async Task Load_AppliesTabFilter_MyPrs_OnlyAuthored()
    {
        var (svc, _, _) = Build();
        var result = await svc.LoadAsync(new TimelineFilter(TimelineTab.MyPrs, null, null));

        Assert.Single(result);
        Assert.Equal(NotificationReason.MyPr, result[0].Reason);
    }

    [Fact]
    public async Task Load_AppliesTabFilter_All_ReturnsEverything()
    {
        var (svc, _, _) = Build();
        var result = await svc.LoadAsync(TimelineFilter.Default);

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public async Task Load_AppliesRepoFilter()
    {
        var (svc, _, _) = Build();
        var result = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, "octocat/repo1", null));

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("octocat/repo1", r.RepositoryFullName));
    }

    [Fact]
    public async Task Load_AppliesSearch_CaseInsensitive_AcrossFields()
    {
        var (svc, _, _) = Build();

        var byTitle = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, null, "MENTION"));
        // Matches: title "Mention me", title "TeamMention!", reason "Mention", reason "TeamMention"
        Assert.True(byTitle.Count >= 2);

        var bySubject = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, null, "Issue"));
        // Issue is the subject_type, matches two rows.
        Assert.Equal(2, bySubject.Count);

        var byRepo = await svc.LoadAsync(new TimelineFilter(TimelineTab.All, null, "ghuboon"));
        Assert.Single(byRepo);
        Assert.Equal("ghuboon/playground", byRepo[0].RepositoryFullName);
    }

    [Fact]
    public async Task Load_OrdersByUpdatedAtDesc()
    {
        var (svc, _, _) = Build();
        var result = await svc.LoadAsync(TimelineFilter.Default);

        for (int i = 1; i < result.Count; i++)
        {
            Assert.True(result[i - 1].UpdatedAt >= result[i].UpdatedAt);
        }
    }

    [Fact]
    public async Task ListRepositoriesAsync_OrdersByFullName()
    {
        var (svc, _, rrepo) = Build();
        rrepo.Repositories.Add(new RepositoryRef("p:b", "primary", "b/repo", "b", "repo", "https://github.com/b/repo"));
        rrepo.Repositories.Add(new RepositoryRef("p:a", "primary", "a/repo", "a", "repo", "https://github.com/a/repo"));
        rrepo.Repositories.Add(new RepositoryRef("o:c", "other", "c/repo", "c", "repo", "https://github.com/c/repo"));

        var list = await svc.ListRepositoriesAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("a/repo", list[0].FullName);
        Assert.Equal("b/repo", list[1].FullName);
    }
}
