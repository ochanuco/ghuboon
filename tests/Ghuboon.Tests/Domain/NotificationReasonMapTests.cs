using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.Domain;

public class NotificationReasonMapTests
{
    [Theory]
    [InlineData("review_requested", NotificationReason.Review)]
    [InlineData("mention", NotificationReason.Mention)]
    [InlineData("team_mention", NotificationReason.TeamMention)]
    [InlineData("assign", NotificationReason.Assigned)]
    [InlineData("author", NotificationReason.MyPr)]
    [InlineData("comment", NotificationReason.Comment)]
    [InlineData("state_change", NotificationReason.State)]
    [InlineData("subscribed", NotificationReason.Watching)]
    [InlineData("manual", NotificationReason.Manual)]
    [InlineData("invitation", NotificationReason.Invitation)]
    [InlineData("security_alert", NotificationReason.SecurityAlert)]
    [InlineData("ci_activity", NotificationReason.CiActivity)]
    public void From_KnownReason_MapsToEnum(string raw, NotificationReason expected)
    {
        Assert.Equal(expected, NotificationReasonMap.From(raw));
    }

    [Theory]
    [InlineData("REVIEW_REQUESTED")]
    [InlineData("Review_Requested")]
    [InlineData("ReViEw_ReQuEsTeD")]
    public void From_IsCaseInsensitive(string raw)
    {
        Assert.Equal(NotificationReason.Review, NotificationReasonMap.From(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_NullOrEmpty_ReturnsUnknown(string? raw)
    {
        Assert.Equal(NotificationReason.Unknown, NotificationReasonMap.From(raw));
    }

    [Theory]
    [InlineData("not_a_real_reason")]
    [InlineData("review")]
    [InlineData("foo")]
    public void From_UnknownString_ReturnsUnknown(string raw)
    {
        Assert.Equal(NotificationReason.Unknown, NotificationReasonMap.From(raw));
    }
}
