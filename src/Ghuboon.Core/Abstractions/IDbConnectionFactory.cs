using System.Data.Common;

namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Opens a configured database connection. Implementations are responsible for
/// applying encryption (SQLCipher PRAGMA key) and ensuring schema migrations
/// have been applied.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Open a new database connection. The returned connection is already
    /// open and ready for queries; callers are responsible for disposing it.
    /// </summary>
    Task<DbConnection> OpenAsync(CancellationToken ct = default);
}
