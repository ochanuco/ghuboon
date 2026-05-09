using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ghuboon.App.ViewModels;

public partial class TimelineItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _unread;

    public TimelineItemViewModel(
        string id,
        string repositoryFullName,
        string title,
        string reason,
        DateTimeOffset updatedAt,
        bool unread)
    {
        Id = id;
        RepositoryFullName = repositoryFullName;
        Title = title;
        Reason = reason;
        UpdatedAt = updatedAt;
        _unread = unread;
    }

    public string Id { get; }

    public string RepositoryFullName { get; }

    public string Title { get; }

    public string Reason { get; }

    public DateTimeOffset UpdatedAt { get; }

    public string UpdatedRelative => FormatRelative(DateTimeOffset.UtcNow - UpdatedAt);

    private static string FormatRelative(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalSeconds < 60)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes}m ago";
        }

        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours}h ago";
        }

        return $"{(int)delta.TotalDays}d ago";
    }
}
