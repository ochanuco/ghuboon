namespace Ghuboon.Core.Domain;

/// <summary>
/// View-side filter for the notification timeline.
/// <para>
/// <see cref="RepositoryFullNames"/> is a multi-select repo filter: rows whose
/// <c>repository_full_name</c> is in the set survive. Null or empty matches
/// every repo (the "All repos" placeholder), so callers don't need a
/// separate "everything" sentinel.
/// </para>
/// </summary>
public sealed record TimelineFilter(
    TimelineTab Tab,
    IReadOnlySet<string>? RepositoryFullNames,
    string? SearchText
)
{
    public static TimelineFilter Default { get; } = new(TimelineTab.All, null, null);

    /// <summary>True when the repo filter is "All repos" (null or empty).</summary>
    public bool MatchesAllRepositories =>
        RepositoryFullNames is null || RepositoryFullNames.Count == 0;
}
