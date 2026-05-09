using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ghuboon.App.ViewModels.Settings;

/// <summary>
/// Sync section of the Settings screen.
/// Sync interval is constant in MVP (5 minutes, ADR-022). The "Sync now" command
/// raises an event that an outer host (e.g., the App composition root or a future
/// <c>INotificationSyncService</c> hookup) can subscribe to. Wave 4 will wire this
/// into the real sync pipeline; for now the command only fires the event.
/// </summary>
public partial class SyncSettingsViewModel : ViewModelBase
{
    public string SyncIntervalDisplay => "Every 5 minutes";

    /// <summary>
    /// Raised when the user clicks "Sync now". Subscribers should trigger an
    /// <c>INotificationSyncService</c> sync. Subscribers must not block this thread;
    /// the VM does not await them.
    /// </summary>
    public event EventHandler? SyncRequested;

    [RelayCommand]
    private void SyncNow()
    {
        // TODO: Wave 4 will wire this to INotificationSyncService.SyncAsync. For now
        // we only raise the event so the App composition root can hook in later.
        SyncRequested?.Invoke(this, EventArgs.Empty);
    }
}
