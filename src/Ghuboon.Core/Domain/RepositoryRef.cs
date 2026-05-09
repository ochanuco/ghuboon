namespace Ghuboon.Core.Domain;

public sealed record RepositoryRef(
    string Id,
    string AccountId,
    string FullName,
    string Owner,
    string Name,
    string HtmlUrl
);
