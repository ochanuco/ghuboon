using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.Domain;

/// <summary>
/// Phase 2 hardening: <see cref="NotificationReason"/> members carry explicit
/// numeric values so that the underlying integer representation stays stable
/// across reorderings or insertions. This test pins those values.
/// </summary>
public class NotificationReasonStabilityTests
{
    [Theory]
    [InlineData(NotificationReason.Unknown, 0)]
    [InlineData(NotificationReason.Review, 1)]
    [InlineData(NotificationReason.Mention, 2)]
    [InlineData(NotificationReason.TeamMention, 3)]
    [InlineData(NotificationReason.Assigned, 4)]
    [InlineData(NotificationReason.MyPr, 5)]
    [InlineData(NotificationReason.Comment, 6)]
    [InlineData(NotificationReason.State, 7)]
    [InlineData(NotificationReason.Watching, 8)]
    [InlineData(NotificationReason.Manual, 9)]
    [InlineData(NotificationReason.Invitation, 10)]
    [InlineData(NotificationReason.SecurityAlert, 11)]
    [InlineData(NotificationReason.CiActivity, 12)]
    public void Member_HasStableNumericValue(NotificationReason member, int expected)
    {
        Assert.Equal(expected, (int)member);
    }

    [Fact]
    public void Members_HaveDistinctNumericValues()
    {
        var members = Enum.GetValues<NotificationReason>();
        var distinct = members.Select(m => (int)m).Distinct().Count();
        Assert.Equal(members.Length, distinct);
    }

    [Fact]
    public void NumericRange_IsContiguousFromZero()
    {
        // Sanity check: the documented numbering goes from 0 (Unknown) up to
        // CiActivity = 12 with no gaps. If a future reorder accidentally drops
        // an explicit value, this prevents silent renumbering.
        var members = Enum.GetValues<NotificationReason>()
            .Select(m => (int)m)
            .OrderBy(v => v)
            .ToArray();

        for (var i = 0; i < members.Length; i++)
        {
            Assert.Equal(i, members[i]);
        }
    }
}
