using Avalonia;

namespace Ghuboon.Tests.Headless;

/// <summary>
/// Minimal <see cref="Application"/> for the headless test harness. We
/// don't reuse the real <c>App</c> class because its composition root
/// pulls in SQLite, Keychain, and the GitHub HTTP client — none of
/// which belong in a unit-style E2E. Tests construct ViewModels
/// directly with stubs and exercise them on the real Avalonia
/// dispatcher via <see cref="HeadlessDispatcherFixture"/>.
/// </summary>
public sealed class HeadlessTestApp : Application
{
}
