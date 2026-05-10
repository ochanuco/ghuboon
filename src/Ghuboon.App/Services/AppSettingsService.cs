using System;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.Services;

/// <summary>
/// Production <see cref="IAppSettingsService"/> backed by <see cref="IAccountRepository"/>
/// (configured account record) and <see cref="IAppSettingsRepository"/> (string key/value
/// preferences).
/// </summary>
public sealed class AppSettingsService : IAppSettingsService
{
    /// <summary>Stable account id used by the MVP single-account UI (ADR-013).</summary>
    public const string PrimaryAccountId = "primary";

    private readonly IAccountRepository _accountRepository;
    private readonly IAppSettingsRepository _settingsRepository;

    private Account? _cachedAccount;

    public AppSettingsService(
        IAccountRepository accountRepository,
        IAppSettingsRepository settingsRepository)
    {
        ArgumentNullException.ThrowIfNull(accountRepository);
        ArgumentNullException.ThrowIfNull(settingsRepository);
        _accountRepository = accountRepository;
        _settingsRepository = settingsRepository;
    }

    public bool IsConfigured => _cachedAccount is not null;

    public async Task<string?> GetPatReferenceAsync()
    {
        var account = await GetPrimaryAccountAsync().ConfigureAwait(false);
        return account?.CredentialKey;
    }

    public async Task<Account?> GetPrimaryAccountAsync(CancellationToken ct = default)
    {
        _cachedAccount = await _accountRepository
            .GetByIdAsync(PrimaryAccountId, ct)
            .ConfigureAwait(false);
        return _cachedAccount;
    }

    public async Task UpsertPrimaryAccountAsync(Account account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!string.Equals(account.Id, PrimaryAccountId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Account.Id must be '{PrimaryAccountId}' for the MVP single-account UI; got '{account.Id}'.",
                nameof(account));
        }
        await _accountRepository.UpsertAsync(account, ct).ConfigureAwait(false);
        _cachedAccount = account;
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var raw = await _settingsRepository.GetAsync(key, ct).ConfigureAwait(false);
        if (raw is not null && bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    public Task SetBoolAsync(string key, bool value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _settingsRepository.SetAsync(key, value.ToString(), ct);
    }
}
