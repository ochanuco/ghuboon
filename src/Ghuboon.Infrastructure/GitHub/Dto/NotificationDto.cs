using System.Text.Json.Serialization;

namespace Ghuboon.Infrastructure.GitHub.Dto;

/// <summary>
/// Wire format of a GitHub notification thread as returned by
/// <c>GET /notifications</c>. Mapped to <see cref="Core.Domain.GitHubNotification"/>
/// via <see cref="NotificationMapper"/>.
/// Only the fields Ghuboon actually consumes are modelled.
/// </summary>
internal sealed record NotificationDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("repository")] RepositoryDto? Repository,
    [property: JsonPropertyName("subject")] SubjectDto? Subject,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("unread")] bool Unread,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("last_read_at")] DateTimeOffset? LastReadAt
);

internal sealed record RepositoryDto(
    [property: JsonPropertyName("full_name")] string? FullName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("owner")] OwnerDto? Owner
);

internal sealed record OwnerDto(
    [property: JsonPropertyName("login")] string? Login
);

internal sealed record SubjectDto(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("latest_comment_url")] string? LatestCommentUrl
);

internal sealed record UserDto(
    [property: JsonPropertyName("login")] string? Login
);
