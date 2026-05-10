using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghuboon.App.Services;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.ViewModels.Settings;

/// <summary>
/// Account section of the Settings screen.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Capture a PAT, validate it via <see cref="IPatValidationService"/>.</item>
///   <item>Persist the PAT to <see cref="ICredentialStore"/> and upsert an
///         <see cref="Account"/> row via <see cref="IAppSettingsService"/>.</item>
///   <item>Clear the PAT input after a successful save so it cannot be redisplayed
///         (ADR-007).</item>
///   <item>Remove the PAT (Keychain delete + clear <c>last_validated_at</c>).</item>
/// </list>
/// </summary>
public partial class AccountSettingsViewModel : ViewModelBase
{
    /// <summary>Stable account id used by the MVP single-account UI (ADR-013).</summary>
    public const string PrimaryAccountId = AppSettingsService.PrimaryAccountId;

    /// <summary>Default credential key for the primary account.</summary>
    public const string PrimaryCredentialKey = "ghuboon.primary";

    /// <summary>Default GitHub host for MVP (ADR-016).</summary>
    public const string DefaultHost = "github.com";

    private readonly IAppSettingsService _appSettings;
    private readonly ICredentialStore _credentialStore;
    private readonly IPatValidationService _validator;
    private readonly TimeProvider _clock;

    [ObservableProperty]
    private string _patInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValidationVisible))]
    [NotifyPropertyChangedFor(nameof(IsValidationValid))]
    [NotifyPropertyChangedFor(nameof(IsValidationInvalid))]
    private PatValidationStatus _validationStatus = PatValidationStatus.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValidationVisible))]
    private string _validationMessage = string.Empty;

    /// <summary>
    /// Informational status message shown independently of the validation traffic
    /// lights (used e.g. after Remove, where ValidationStatus is intentionally
    /// reset to None).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusMessageVisible))]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccount))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private string? _currentLogin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccount))]
    private string? _credentialKey;

    [ObservableProperty]
    private DateTimeOffset? _lastValidatedAt;

    public AccountSettingsViewModel(
        IAppSettingsService appSettings,
        ICredentialStore credentialStore,
        IPatValidationService validator,
        TimeProvider? clock = null)
    {
        _appSettings = appSettings;
        _credentialStore = credentialStore;
        _validator = validator;
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsValidationVisible => ValidationStatus != PatValidationStatus.None
        && !string.IsNullOrEmpty(ValidationMessage);

    public bool IsValidationValid => ValidationStatus == PatValidationStatus.Valid;

    public bool IsValidationInvalid => ValidationStatus == PatValidationStatus.Invalid;

    public bool IsStatusMessageVisible => !string.IsNullOrEmpty(StatusMessage);

    public bool HasAccount => !string.IsNullOrEmpty(CredentialKey);

    /// <summary>
    /// Refreshes the displayed account info (login, credential key, last-validated)
    /// from the underlying repositories.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var account = await _appSettings.GetPrimaryAccountAsync(ct).ConfigureAwait(false);
        CurrentLogin = string.IsNullOrEmpty(account?.Login) ? null : account!.Login;
        CredentialKey = account?.CredentialKey;
        LastValidatedAt = account?.LastValidatedAt;
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task ValidateAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            // Snapshot the input so a concurrent edit cannot desync the
            // value-being-validated from the field state we read on completion.
            // Trim because pasting from a browser often grabs trailing whitespace.
            var pat = PatInput?.Trim() ?? string.Empty;
            var outcome = await _validator.ValidateAsync(pat, ct).ConfigureAwait(true);
            ApplyOutcome(outcome);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task SaveAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            // Snapshot the input up front so a concurrent edit cannot cause
            // the saved credential to disagree with the validated PAT.
            // Trim because pasting from a browser often grabs trailing whitespace.
            var pat = PatInput?.Trim() ?? string.Empty;
            var outcome = await _validator.ValidateAsync(pat, ct).ConfigureAwait(true);
            ApplyOutcome(outcome);

            if (outcome.Status != PatValidationStatus.Valid)
            {
                return;
            }

            // Persist credential and account record. ICredentialStore writes to the
            // OS keychain (ADR-007); the PAT never lives anywhere else.
            await _credentialStore
                .SetAsync(PrimaryCredentialKey, pat, ct)
                .ConfigureAwait(true);

            var now = _clock.GetUtcNow();
            var existing = await _appSettings.GetPrimaryAccountAsync(ct).ConfigureAwait(true);
            var account = new Account(
                Id: PrimaryAccountId,
                Host: DefaultHost,
                Login: outcome.Login ?? existing?.Login ?? string.Empty,
                CredentialKey: PrimaryCredentialKey,
                CreatedAt: existing?.CreatedAt ?? now,
                LastValidatedAt: now);

            await _appSettings.UpsertPrimaryAccountAsync(account, ct).ConfigureAwait(true);

            CurrentLogin = string.IsNullOrEmpty(account.Login) ? null : account.Login;
            CredentialKey = account.CredentialKey;
            LastValidatedAt = account.LastValidatedAt;

            // Clear the input so the saved token cannot be redisplayed (ADR-007).
            PatInput = string.Empty;
            ValidationMessage = $"Saved for @{account.Login}.";
            ValidationStatus = PatValidationStatus.Valid;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var account = await _appSettings.GetPrimaryAccountAsync(ct).ConfigureAwait(true);
            var key = account?.CredentialKey ?? PrimaryCredentialKey;

            await _credentialStore.DeleteAsync(key, ct).ConfigureAwait(true);

            if (account is not null)
            {
                var cleared = account with { LastValidatedAt = null };
                await _appSettings
                    .UpsertPrimaryAccountAsync(cleared, ct)
                    .ConfigureAwait(true);
            }

            CurrentLogin = null;
            LastValidatedAt = null;
            PatInput = string.Empty;
            // Reset the validate-traffic-light, but surface the removal as a
            // distinct StatusMessage so the user still sees confirmation
            // (IsValidationVisible suppresses ValidationMessage when status==None).
            ValidationStatus = PatValidationStatus.None;
            ValidationMessage = string.Empty;
            StatusMessage = "Token removed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInteract() => !IsBusy;

    private bool CanRemove() => !IsBusy && !string.IsNullOrEmpty(CredentialKey);

    private void ApplyOutcome(PatValidationOutcome outcome)
    {
        ValidationStatus = outcome.Status;
        ValidationMessage = outcome.Message;
        // Clear any stale removal/info status so the validation result is what
        // the user sees.
        StatusMessage = string.Empty;
    }
}
