using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.Domain;

public class TimelineFilterTests
{
    [Fact]
    public void Default_IsAllTabWithNoFilters()
    {
        var f = TimelineFilter.Default;

        Assert.Equal(TimelineTab.All, f.Tab);
        Assert.Null(f.RepositoryFullNames);
        Assert.Null(f.SearchText);
        Assert.True(f.MatchesAllRepositories);
    }

    [Fact]
    public void RecordEquality_HoldsForSameValues()
    {
        var a = new TimelineFilter(TimelineTab.Review, new HashSet<string> { "octocat/spoon" }, "needle");
        var b = new TimelineFilter(TimelineTab.Review, new HashSet<string> { "octocat/spoon" }, "needle");

        // Records compare reference identity for IReadOnlySet members, so
        // equality on the same logical set instance is what records guarantee.
        var c = a;
        Assert.Equal(a, c);
        Assert.Equal(a.GetHashCode(), c.GetHashCode());

        // Two independently-allocated filters with the same field VALUES
        // but distinct set references are NOT equal — that's the
        // documented record-equality limitation we accept. Callers should
        // reuse instances when they want equality. Pin the limitation
        // here so a future change to "deep-equality for collections"
        // doesn't silently flip the contract.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DiffersWhenAnyFieldChanges()
    {
        var set = new HashSet<string> { "octocat/spoon" };
        var a = new TimelineFilter(TimelineTab.Review, set, "needle");
        var b = a with { Tab = TimelineTab.MyPrs };
        var c = a with { RepositoryFullNames = null };
        var d = a with { SearchText = "other" };

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public void WithExpression_ProducesNewInstance()
    {
        var a = TimelineFilter.Default;
        var b = a with { Tab = TimelineTab.Mention };

        Assert.NotSame(a, b);
        Assert.Equal(TimelineTab.All, a.Tab);
        Assert.Equal(TimelineTab.Mention, b.Tab);
    }

    [Fact]
    public void MatchesAllRepositories_TrueForNullOrEmpty()
    {
        Assert.True(TimelineFilter.Default.MatchesAllRepositories);
        Assert.True((TimelineFilter.Default with { RepositoryFullNames = new HashSet<string>() }).MatchesAllRepositories);
        Assert.False((TimelineFilter.Default with { RepositoryFullNames = new HashSet<string> { "octocat/spoon" } }).MatchesAllRepositories);
    }
}
