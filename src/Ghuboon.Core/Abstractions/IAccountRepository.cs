using Ghuboon.Core.Domain;

namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Persistence for configured GitHub accounts (ADR-013).
/// </summary>
public interface IAccountRepository
{
    Task UpsertAsync(Account account, CancellationToken ct = default);

    Task<Account?> GetByIdAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default);
}
