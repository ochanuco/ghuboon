using Ghuboon.Core.Domain;

namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Persistence for the repositories an account has notifications for.
/// </summary>
public interface IRepositoryRepository
{
    Task UpsertAsync(RepositoryRef repository, CancellationToken ct = default);

    Task<RepositoryRef?> GetByFullNameAsync(string accountId, string fullName, CancellationToken ct = default);

    Task<IReadOnlyList<RepositoryRef>> ListByAccountAsync(string accountId, CancellationToken ct = default);
}
