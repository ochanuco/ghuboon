using Ghuboon.Core.Abstractions;

namespace Ghuboon.Infrastructure.Credentials;

/// <summary>
/// Placeholder for ADR-007 future Windows Credential Manager support. Throws on every call
/// so accidental Windows-platform use surfaces immediately rather than silently no-op'ing.
/// </summary>
public sealed class WindowsCredentialStorePlaceholder : ICredentialStore
{
    private const string Message =
        "Windows Credential Manager backend is not implemented yet. See ADR-007.";

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => throw new PlatformNotSupportedException(Message);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
        => throw new PlatformNotSupportedException(Message);

    public Task DeleteAsync(string key, CancellationToken ct = default)
        => throw new PlatformNotSupportedException(Message);
}
