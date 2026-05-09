using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghuboon.Core.Abstractions;

namespace Ghuboon.App.ViewModels.Settings;

/// <summary>
/// Cache section of the Settings screen.
///
/// Retention is a fixed 30 days in MVP (ADR-022). "Clear local cache" wipes every
/// cached notification by calling <see cref="INotificationRepository.DeleteOlderThanAsync"/>
/// with a cutoff a few seconds in the future, deleting all rows.
///
/// TODO: A future <c>INotificationRepository.ClearAsync()</c> would make the intent
/// explicit; for now we route through the existing abstraction so that this lane
/// does not introduce a Core change concurrent lanes are not expecting.
/// </summary>
public partial class CacheSettingsViewModel : ViewModelBase
{
    private readonly INotificationRepository _notifications;
    private readonly TimeProvider _clock;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCacheCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _lastDeletedRowCount;

    public CacheSettingsViewModel(INotificationRepository notifications, TimeProvider? clock = null)
    {
        _notifications = notifications;
        _clock = clock ?? TimeProvider.System;
    }

    public string CacheRetentionDisplay => "30 days";

    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearCacheAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            // Cutoff slightly in the future deletes every existing row.
            var cutoff = _clock.GetUtcNow().AddSeconds(1);
            var deleted = await _notifications
                .DeleteOlderThanAsync(cutoff, ct)
                .ConfigureAwait(true);

            LastDeletedRowCount = deleted;
            StatusMessage = deleted == 1
                ? "Cleared 1 cached notification."
                : $"Cleared {deleted} cached notifications.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanClear() => !IsBusy;
}
