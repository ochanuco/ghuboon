using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.Domain;

public class TimelineFilterTests
{
    [Fact]
    public void Default_IsAllTabWithNoFilters()
    {
        var f = TimelineFilter.Default;

        Assert.Equal(TimelineTab.All, f.Tab);
        Assert.Null(f.RepositoryFullName);
        Assert.Null(f.SearchText);
    }

    [Fact]
    public void RecordEquality_HoldsForSameValues()
    {
        var a = new TimelineFilter(TimelineTab.Review, "octocat/spoon", "needle");
        var b = new TimelineFilter(TimelineTab.Review, "octocat/spoon", "needle");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void RecordEquality_DiffersWhenAnyFieldChanges()
    {
        var a = new TimelineFilter(TimelineTab.Review, "octocat/spoon", "needle");
        var b = a with { Tab = TimelineTab.MyPrs };
        var c = a with { RepositoryFullName = null };
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
}
