using System.Runtime.InteropServices;

namespace Ghuboon.Tests.Infrastructure;

/// <summary>
/// xUnit <see cref="FactAttribute"/> that skips the test on non-macOS hosts.
/// </summary>
/// <remarks>
/// The macOS Keychain backend shells out to <c>/usr/bin/security</c>, which only
/// exists on Darwin. Tests that exercise the live Keychain must skip cleanly on
/// Linux/Windows CI rather than silently early-returning, so failures show up as
/// "Skipped" rather than "Passed" and the gap is honest in the test report.
/// </remarks>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Skip = "Requires macOS Keychain.";
        }
    }
}
