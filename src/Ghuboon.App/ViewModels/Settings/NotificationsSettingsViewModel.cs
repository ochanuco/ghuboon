using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // Issue #43: capture the construction-time scheduler so persistence
    // continuations (which mutate observable properties and therefore raise
    // PropertyChanged) run on the UI thread. When the VM is constructed off a
    // SynchronizationContext (typical in unit tests), we fall back to a
    // synchronous, default-scheduler continuation so tests can observe the
    // reverted state without a UI dispatcher.
    private readonly TaskScheduler _persistContinuationScheduler;
    private readonly TaskContinuationOptions _persistContinuationOptions;
    private bool _suppressPersist;

    [ObservableProperty]
    private bool _osNotificationsEnabled = true;

    /// <summary>
    /// Surfaces a persistence error so the UI can show it. Empty when the last
    /// toggle persisted successfully.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPersistError))]
    private string _persistErrorMessage = string.Empty;

    public bool HasPersistError => !string.IsNullOrEmpty(PersistErrorMessage);

    public NotificationsSettingsViewModel(IAppSettingsService appSettings)
    {
        _appSettings = appSettings;

        // Capture the UI synchronization context if we're constructed on one
        // (e.g., the Avalonia UI thread). Off-context construction (tests)
        // falls back to running the continuation synchronously on the
        // completing task's thread; that keeps deterministic test behavior.
        if (SynchronizationContext.Current is not null)
        {
            _persistContinuationScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            _persistContinuationOptions = TaskContinuationOptions.None;
        }
        else
        {
            _persistContinuationScheduler = TaskScheduler.Default;
            _persistContinuationOptions = TaskContinuationOptions.ExecuteSynchronously;
        }
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

        // Persistence failures must surface to the UI (Issue #18). Attach a
        // continuation that flips the toggle back and exposes the error message
        // when the write fails. Issue #43: schedule the continuation on the
        // captured UI synchronization context (when present) so PropertyChanged
        // events for PersistErrorMessage and OsNotificationsEnabled — and the
        // _suppressPersist mutation — are raised on the UI thread.
        var task = _appSettings.SetBoolAsync(OsNotificationsEnabledKey, value);
        _ = task.ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                {
                    var ex = t.Exception?.GetBaseException();
                    Trace.WriteLine($"NotificationsSettingsViewModel: failed to persist '{OsNotificationsEnabledKey}': {ex?.GetType().Name}: {ex?.Message}");
                    PersistErrorMessage = ex?.Message ?? "Failed to save notification setting.";
                    // Revert the toggle so the UI reflects what's persisted.
                    _suppressPersist = true;
                    try
                    {
                        OsNotificationsEnabled = !value;
                    }
                    finally
                    {
                        _suppressPersist = false;
                    }
                }
                else if (t.IsCompletedSuccessfully)
                {
                    PersistErrorMessage = string.Empty;
                }
            },
            CancellationToken.None,
            _persistContinuationOptions,
            _persistContinuationScheduler);
    }
}
