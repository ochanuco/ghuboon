using System.Threading.Tasks;

namespace Ghuboon.App.Services;

public sealed class StubAppSettingsService : IAppSettingsService
{
    public bool IsConfigured => false;

    public Task<string?> GetPatReferenceAsync() => Task.FromResult<string?>(null);
}
