namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Simple persisted key/value app settings (theme prefs, last selected tab, ...).
/// Values are stored as strings; callers handle (de)serialization.
/// </summary>
public interface IAppSettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, CancellationToken ct = default);
}
