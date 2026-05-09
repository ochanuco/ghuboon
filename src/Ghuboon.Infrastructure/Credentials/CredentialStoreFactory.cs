using System.Runtime.InteropServices;
using Ghuboon.Core.Abstractions;

namespace Ghuboon.Infrastructure.Credentials;

/// <summary>
/// Selects an <see cref="ICredentialStore"/> implementation for the current OS.
/// </summary>
public static class CredentialStoreFactory
{
    public static ICredentialStore Create(string service = "com.ghuboon.dev")
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new KeychainCredentialStore(service);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsCredentialStorePlaceholder();
        }

        throw new PlatformNotSupportedException(
            $"No credential store implementation for {RuntimeInformation.OSDescription}.");
    }
}
