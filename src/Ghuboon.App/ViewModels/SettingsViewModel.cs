using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels.Settings;
using Ghuboon.Core.Abstractions;

namespace Ghuboon.App.ViewModels;

/// <summary>
/// Root ViewModel for the Settings screen.
///
/// Composes one sub-VM per section (Account, Sync, Notifications, Cache, Security,
/// About). Each sub-VM owns its own state and commands; the root VM only handles
/// composition, the parameterless constructor used by the Avalonia previewer, and
/// the <see cref="LoadAsync"/> entry point used by the host.
///
/// Note: Lane G intentionally leaves the App composition root almost untouched.
/// When the App is wired with real abstractions, callers should use the rich
/// constructor; the parameterless constructor delegates to in-memory stubs so the
/// previewer can render without a database or network.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettingsService _appSettings;

    public SettingsViewModel()
        : this(Default())
    {
    }

    public SettingsViewModel(IAppSettingsService appSettings)
        : this(BuildDefault(NullCheck(appSettings)))
    {
    }

    private static IAppSettingsService NullCheck(IAppSettingsService appSettings)
    {
        ArgumentNullException.ThrowIfNull(appSettings);
        return appSettings;
    }

    public SettingsViewModel(SettingsViewModelDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        _appSettings = deps.AppSettings;
        Account = new AccountSettingsViewModel(
            deps.AppSettings,
            deps.CredentialStore,
            deps.PatValidation,
            deps.Clock);
        Sync = new SyncSettingsViewModel();
        Notifications = new NotificationsSettingsViewModel(deps.AppSettings);
        Cache = new CacheSettingsViewModel(deps.NotificationRepository, deps.Clock);
        Security = new SecuritySettingsViewModel();
        About = new AboutSettingsViewModel(deps.OpenBrowser);
    }

    public AccountSettingsViewModel Account { get; }
    public SyncSettingsViewModel Sync { get; }
    public NotificationsSettingsViewModel Notifications { get; }
    public CacheSettingsViewModel Cache { get; }
    public SecuritySettingsViewModel Security { get; }
    public AboutSettingsViewModel About { get; }

    public bool IsConfigured => _appSettings.IsConfigured;

    /// <summary>
    /// Loads persisted state for sections that need it. Safe to call multiple times.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await Account.RefreshAsync(ct).ConfigureAwait(false);
        await Notifications.LoadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Convenience factory used by the design-time previewer.
    /// </summary>
    public static SettingsViewModelDependencies Default()
    {
        var appSettings = new StubAppSettingsService();
        return BuildDefault(appSettings);
    }

    private static SettingsViewModelDependencies BuildDefault(IAppSettingsService appSettings)
    {
        var credentialStore = new InMemoryDesignCredentialStore();
        var apiClient = new DesignTimeGitHubApiClient();
        var notifications = new EmptyNotificationRepository();
        return new SettingsViewModelDependencies(
            AppSettings: appSettings,
            CredentialStore: credentialStore,
            PatValidation: new PatValidationService(apiClient),
            NotificationRepository: notifications,
            OpenBrowser: AboutSettingsViewModel.DefaultOpenBrowser,
            Clock: TimeProvider.System);
    }

    /// <summary>Aggregate of dependencies the Settings screen needs.</summary>
    public sealed record SettingsViewModelDependencies(
        IAppSettingsService AppSettings,
        ICredentialStore CredentialStore,
        IPatValidationService PatValidation,
        INotificationRepository NotificationRepository,
        Action<string> OpenBrowser,
        TimeProvider Clock);

    private sealed class InMemoryDesignCredentialStore : ICredentialStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class DesignTimeGitHubApiClient : IGitHubApiClient
    {
        public Task<UserValidationResult> ValidateAsync(string pat, CancellationToken ct = default)
            => Task.FromResult(new UserValidationResult(false, null, ErrorCategory.Auth, "Design-time stub."));

        public Task<NotificationsResponse> ListNotificationsAsync(string pat, NotificationsRequest request, CancellationToken ct = default)
            => Task.FromResult(new NotificationsResponse(Array.Empty<Ghuboon.Core.Domain.GitHubNotification>(), null, RateLimitInfo.Empty, false));

        public Task MarkThreadReadAsync(string pat, string threadId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string?> GetSubjectBodyAsync(string pat, string subjectApiUrl, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> GetThreadSubjectUrlAsync(string pat, string threadId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> GetLatestCommentBodyAsync(string pat, string threadId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<(string? Body, string? AuthorLogin)> GetLatestCommentDetailsAsync(string pat, string threadId, CancellationToken ct = default)
            => Task.FromResult<(string?, string?)>((null, null));
    }

    private sealed class EmptyNotificationRepository : INotificationRepository
    {
        public Task UpsertAsync(Ghuboon.Core.Domain.GitHubNotification notification, string rawJson, DateTimeOffset syncedAt, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<Ghuboon.Core.Domain.GitHubNotification?> GetByIdAsync(string id, CancellationToken ct = default)
            => Task.FromResult<Ghuboon.Core.Domain.GitHubNotification?>(null);

        public Task<IReadOnlyList<Ghuboon.Core.Domain.GitHubNotification>> ListByAccountAsync(string accountId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Ghuboon.Core.Domain.GitHubNotification>>(Array.Empty<Ghuboon.Core.Domain.GitHubNotification>());

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> SetActorLoginAsync(string id, string actorLogin, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
