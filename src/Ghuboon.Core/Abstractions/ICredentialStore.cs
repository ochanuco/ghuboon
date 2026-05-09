namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Abstraction over the OS credential store. Implementations must never log values.
/// </summary>
public interface ICredentialStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}
