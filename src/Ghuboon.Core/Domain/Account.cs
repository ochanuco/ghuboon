namespace Ghuboon.Core.Domain;

/// <summary>
/// A configured GitHub account. Schema is multi-account aware (ADR-013) even though
/// MVP UI exposes only one. <see cref="CredentialKey"/> references an entry in the
/// OS credential store; the PAT itself never lives on this record.
/// </summary>
public sealed record Account(
    string Id,
    string Host,
    string Login,
    string CredentialKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastValidatedAt
);
