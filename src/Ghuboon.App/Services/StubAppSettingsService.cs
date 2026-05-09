using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.Services;

/// <summary>
/// In-memory <see cref="IAppSettingsService"/> used while the application boots
/// without an encrypted database, and for design-time previews. It does not persist
/// data across runs and intentionally never accepts a PAT — credential storage is
/// always handled by <see cref="Ghuboon.Core.Abstractions.ICredentialStore"/>.
/// </summary>
public sealed class StubAppSettingsService : IAppSettingsService
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private Account? _account;

    public bool IsConfigured => _account is not null;

    public Task<string?> GetPatReferenceAsync()
        => Task.FromResult<string?>(_account?.CredentialKey);

    public Task<Account?> GetPrimaryAccountAsync(CancellationToken ct = default)
        => Task.FromResult(_account);

    public Task UpsertPrimaryAccountAsync(Account account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        _account = account;
        return Task.CompletedTask;
    }

    public Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct = default)
    {
        if (_values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed))
        {
            return Task.FromResult(parsed);
        }

        return Task.FromResult(defaultValue);
    }

    public Task SetBoolAsync(string key, bool value, CancellationToken ct = default)
    {
        _values[key] = value.ToString();
        return Task.CompletedTask;
    }
}
