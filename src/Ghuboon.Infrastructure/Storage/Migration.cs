namespace Ghuboon.Infrastructure.Storage;

/// <summary>
/// A schema migration. Versions are monotonically increasing integers; the runner
/// applies any migration whose version has not yet been recorded in
/// <c>_schema_migrations</c>.
/// </summary>
public sealed record Migration(int Version, string Name, string Sql);
