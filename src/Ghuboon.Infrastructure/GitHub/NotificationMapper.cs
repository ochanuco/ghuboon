using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.GitHub.Dto;

namespace Ghuboon.Infrastructure.GitHub;

/// <summary>
/// Maps GitHub Notifications API DTOs to Ghuboon's domain types (ADR-009 / Phase 6).
/// Keeps DTOs internal to the Infrastructure layer.
/// </summary>
internal static class NotificationMapper
{
    /// <summary>
    /// Maps a single notification DTO. Returns null when required fields are missing
    /// (defensive against API drift; ADR-016 — no GHES-specific schema handling).
    /// </summary>
    public static GitHubNotification? Map(NotificationDto dto, string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (dto is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            return null;
        }

        var repoFullName = dto.Repository?.FullName
            ?? (dto.Repository is { Owner.Login: { } login, Name: { } name }
                ? $"{login}/{name}"
                : null);

        if (string.IsNullOrWhiteSpace(repoFullName))
        {
            return null;
        }

        var subjectType = dto.Subject?.Type ?? string.Empty;
        var subjectTitle = dto.Subject?.Title ?? string.Empty;
        var subjectApiUrl = dto.Subject?.Url;

        var webUrl = SubjectUrlConverter.ToWebUrl(subjectApiUrl, subjectType)
                      ?? dto.Repository?.HtmlUrl;

        var subject = new NotificationSubject(
            Type: subjectType,
            Title: subjectTitle,
            ApiUrl: subjectApiUrl,
            WebUrl: webUrl
        );

        var reason = NotificationReasonMap.From(dto.Reason);
        var updatedAt = dto.UpdatedAt ?? DateTimeOffset.MinValue;

        return new GitHubNotification(
            Id: $"{accountId}:{dto.Id}",
            AccountId: accountId,
            ThreadId: dto.Id,
            RepositoryFullName: repoFullName,
            Subject: subject,
            Reason: reason,
            Unread: dto.Unread,
            UpdatedAt: updatedAt,
            LastReadAt: dto.LastReadAt
        );
    }

    /// <summary>
    /// Maps a list of notification DTOs, skipping any that cannot be mapped.
    /// </summary>
    public static IReadOnlyList<GitHubNotification> Map(IEnumerable<NotificationDto> dtos, string accountId)
    {
        ArgumentNullException.ThrowIfNull(dtos);
        var result = new List<GitHubNotification>();
        foreach (var dto in dtos)
        {
            var mapped = Map(dto, accountId);
            if (mapped is not null)
            {
                result.Add(mapped);
            }
        }
        return result;
    }
}
