using CommunityToolkit.Mvvm.ComponentModel;

namespace Ghuboon.App.ViewModels;

/// <summary>
/// One repository entry in the multi-select repo filter dropdown. The
/// <see cref="IsSelected"/> property is bound to a <c>CheckBox</c> in the
/// popup; <see cref="MainWindowViewModel"/> listens for the toggle and
/// rebuilds the timeline filter accordingly.
/// </summary>
public partial class RepositoryFilterItem : ObservableObject
{
    public RepositoryFilterItem(string fullName)
    {
        FullName = fullName ?? throw new System.ArgumentNullException(nameof(fullName));
    }

    public string FullName { get; }

    [ObservableProperty]
    private bool _isSelected;
}
