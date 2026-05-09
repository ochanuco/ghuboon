using System.Threading;
using System.Threading.Tasks;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.Services;

/// <summary>
/// App-level settings facade used by the Settings UI and the rest of the App layer.
/// Backed by <see cref="Ghuboon.Core.Abstractions.IAppSettingsRepository"/> for
/// generic key/value preferences, and <see cref="Ghuboon.Core.Abstractions.IAccountRepository"/>
/// for the configured GitHub account record.
/// </summary>
public interface IAppSettingsService
{
    /// <summary>
    /// True when a primary account has been configured (a row exists in the account
    /// repository). Note this does not verify that a PAT is currently valid.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Returns the credential key (Keychain identifier) for the primary account,
    /// or <c>null</c> when no account has been configured. The PAT itself is never
    /// returned through this interface (ADR-007).
    /// </summary>
    Task<string?> GetPatReferenceAsync();

    /// <summary>
    /// Loads the primary account record, refreshing <see cref="IsConfigured"/>.
    /// </summary>
    Task<Account?> GetPrimaryAccountAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists the primary account record (insert or update).
    /// </summary>
    Task UpsertPrimaryAccountAsync(Account account, CancellationToken ct = default);

    /// <summary>
    /// Reads a boolean preference. Returns <paramref name="defaultValue"/> when the
    /// underlying setting does not exist or cannot be parsed.
    /// </summary>
    Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct = default);

    /// <summary>
    /// Persists a boolean preference.
    /// </summary>
    Task SetBoolAsync(string key, bool value, CancellationToken ct = default);
}
