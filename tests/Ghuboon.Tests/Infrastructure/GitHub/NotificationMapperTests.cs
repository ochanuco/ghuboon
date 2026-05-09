using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.GitHub;
using Ghuboon.Infrastructure.GitHub.Dto;

namespace Ghuboon.Tests.Infrastructure.GitHub;

public class NotificationMapperTests
{
    [Fact]
    public void Map_pull_request_subject_produces_pull_web_url()
    {
        var dto = new NotificationDto(
            Id: "1",
            Repository: new RepositoryDto("octo/hello", "hello", "https://github.com/octo/hello", new OwnerDto("octo")),
            Subject: new SubjectDto("My PR", "PullRequest", "https://api.github.com/repos/octo/hello/pulls/12", null),
            Reason: "review_requested",
            Unread: true,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            LastReadAt: null);

        var mapped = NotificationMapper.Map(dto, "acct");

        Assert.NotNull(mapped);
        Assert.Equal("acct:1", mapped!.Id);
        Assert.Equal("acct", mapped.AccountId);
        Assert.Equal("1", mapped.ThreadId);
        Assert.Equal("octo/hello", mapped.RepositoryFullName);
        Assert.Equal(NotificationReason.Review, mapped.Reason);
        Assert.Equal("PullRequest", mapped.Subject.Type);
        Assert.Equal("https://github.com/octo/hello/pull/12", mapped.Subject.WebUrl);
        Assert.True(mapped.Unread);
    }

    [Fact]
    public void Map_issue_subject_produces_issues_web_url()
    {
        var dto = new NotificationDto(
            Id: "2",
            Repository: new RepositoryDto("octo/hello", "hello", "https://github.com/octo/hello", new OwnerDto("octo")),
            Subject: new SubjectDto("Bug", "Issue", "https://api.github.com/repos/octo/hello/issues/77", null),
            Reason: "mention",
            Unread: false,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            LastReadAt: DateTimeOffset.UnixEpoch);

        var mapped = NotificationMapper.Map(dto, "acct");

        Assert.NotNull(mapped);
        Assert.Equal(NotificationReason.Mention, mapped!.Reason);
        Assert.Equal("https://github.com/octo/hello/issues/77", mapped.Subject.WebUrl);
        Assert.False(mapped.Unread);
        Assert.Equal(DateTimeOffset.UnixEpoch, mapped.LastReadAt);
    }

    [Fact]
    public void Map_release_subject_produces_releases_web_url()
    {
        var dto = new NotificationDto(
            Id: "3",
            Repository: new RepositoryDto("octo/hello", "hello", "https://github.com/octo/hello", new OwnerDto("octo")),
            Subject: new SubjectDto("v1.0", "Release", "https://api.github.com/repos/octo/hello/releases/100", null),
            Reason: "subscribed",
            Unread: true,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            LastReadAt: null);

        var mapped = NotificationMapper.Map(dto, "acct");

        Assert.NotNull(mapped);
        Assert.Equal(NotificationReason.Watching, mapped!.Reason);
        Assert.Equal("https://github.com/octo/hello/releases/100", mapped.Subject.WebUrl);
    }

    [Fact]
    public void Map_unknown_reason_falls_back_to_unknown_enum()
    {
        var dto = new NotificationDto(
            Id: "4",
            Repository: new RepositoryDto("octo/hello", "hello", null, new OwnerDto("octo")),
            Subject: new SubjectDto("?", "Unknown", null, null),
            Reason: "totally_new_reason",
            Unread: true,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            LastReadAt: null);

        var mapped = NotificationMapper.Map(dto, "acct");

        Assert.NotNull(mapped);
        Assert.Equal(NotificationReason.Unknown, mapped!.Reason);
        // Subject without an api url falls back to repo html_url; null in this case is fine.
        Assert.Null(mapped.Subject.WebUrl);
    }

    [Fact]
    public void Map_falls_back_to_repository_html_url_when_subject_url_unmappable()
    {
        var dto = new NotificationDto(
            Id: "5",
            Repository: new RepositoryDto("octo/hello", "hello", "https://github.com/octo/hello", new OwnerDto("octo")),
            Subject: new SubjectDto("Misc", "CheckSuite", "https://something.else/path", null),
            Reason: "ci_activity",
            Unread: true,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            LastReadAt: null);

        var mapped = NotificationMapper.Map(dto, "acct");

        Assert.NotNull(mapped);
        Assert.Equal(NotificationReason.CiActivity, mapped!.Reason);
        Assert.Equal("https://github.com/octo/hello", mapped.Subject.WebUrl);
    }

    [Fact]
    public void Map_synthesizes_full_name_from_owner_and_name_when_full_name_missing()
    {
        var dto = new NotificationDto(
            Id: "6",
            Repository: new RepositoryDto(FullName: null, Name: "hello", HtmlUrl: null, Owner: new OwnerDto("octo")),
            Subject: new SubjectDto("Issue", "Issue", "https://api.github.com/repos/octo/hello/issues/1", null),
            Reason: "assign",
            Unread: true,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            LastReadAt: null);

        var mapped = NotificationMapper.Map(dto, "acct");

        Assert.NotNull(mapped);
        Assert.Equal("octo/hello", mapped!.RepositoryFullName);
        Assert.Equal(NotificationReason.Assigned, mapped.Reason);
    }

    [Fact]
    public void Map_returns_null_when_id_missing()
    {
        var dto = new NotificationDto(
            Id: "",
            Repository: new RepositoryDto("octo/hello", "hello", null, new OwnerDto("octo")),
            Subject: new SubjectDto("x", "Issue", null, null),
            Reason: "mention",
            Unread: true,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            LastReadAt: null);

        var mapped = NotificationMapper.Map(dto, "acct");

        Assert.Null(mapped);
    }

    [Fact]
    public void Map_returns_null_when_repository_unidentifiable()
    {
        var dto = new NotificationDto(
            Id: "7",
            Repository: null,
            Subject: new SubjectDto("x", "Issue", null, null),
            Reason: "mention",
            Unread: true,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            LastReadAt: null);

        var mapped = NotificationMapper.Map(dto, "acct");

        Assert.Null(mapped);
    }

    [Fact]
    public void MapList_skips_invalid_dtos()
    {
        var dtos = new List<NotificationDto>
        {
            new("1", new RepositoryDto("octo/hello", "hello", null, new OwnerDto("octo")), new SubjectDto("a", "Issue", "https://api.github.com/repos/octo/hello/issues/1", null), "mention", true, DateTimeOffset.UnixEpoch, null),
            new("", new RepositoryDto("octo/hello", "hello", null, new OwnerDto("octo")), new SubjectDto("a", "Issue", null, null), "mention", true, DateTimeOffset.UnixEpoch, null),
        };

        var mapped = NotificationMapper.Map(dtos, "acct");

        Assert.Single(mapped);
    }
}
