using System.Threading.Tasks;

namespace Ghuboon.App.Services;

public interface IAppSettingsService
{
    bool IsConfigured { get; }

    Task<string?> GetPatReferenceAsync();
}
