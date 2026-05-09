using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Ghuboon.App.Services;

namespace Ghuboon.App.ViewModels.Settings;

/// <summary>
/// Notifications section of the Settings screen.
/// Toggles whether OS notifications are emitted for high-priority reasons
/// (review_requested, mention, team_mention, assign — ADR-008/Phase 11).
/// </summary>
public partial class NotificationsSettingsViewModel : ViewModelBase
{
    /// <summary>App settings key for the OS-notifications toggle.</summary>
    public const string OsNotificationsEnabledKey = "notifications.os.enabled";

    private readonly IAppSettingsService _appSettings;
    private bool _suppressPersist;

    [ObservableProperty]
    private bool _osNotificationsEnabled = true;

    public NotificationsSettingsViewModel(IAppSettingsService appSettings)
    {
        _appSettings = appSettings;
    }

    /// <summary>
    /// Reasons that produce OS notifications, displayed as read-only text in the UI.
    /// Mirrors the high-priority list from ADR-008.
    /// </summary>
    public IReadOnlyList<string> HighPriorityReasons { get; } = new[]
    {
        "Review requested",
        "Mention",
        "Team mention",
        "Assigned",
    };

    /// <summary>
    /// Loads the persisted toggle without triggering a write back through the
    /// <c>partial void OnXChanged</c> hook.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var value = await _appSettings
            .GetBoolAsync(OsNotificationsEnabledKey, defaultValue: true, ct)
            .ConfigureAwait(false);

        _suppressPersist = true;
        try
        {
            OsNotificationsEnabled = value;
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    partial void OnOsNotificationsEnabledChanged(bool value)
    {
        if (_suppressPersist)
        {
            return;
        }

        // Fire-and-forget: persistence failures should not crash the UI thread; the
        // surface for surfacing errors is the status bar (Phase 14). We capture the
        // task to avoid analyzer noise but do not await it.
        _ = _appSettings.SetBoolAsync(OsNotificationsEnabledKey, value);
    }
}
