namespace Ghuboon.Core.Domain;

/// <summary>
/// View-side filter for the notification timeline.
/// </summary>
public sealed record TimelineFilter(
    TimelineTab Tab,
    string? RepositoryFullName,
    string? SearchText
)
{
    public static TimelineFilter Default { get; } = new(TimelineTab.All, null, null);
}
