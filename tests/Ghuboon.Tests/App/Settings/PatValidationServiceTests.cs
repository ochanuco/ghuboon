using Ghuboon.App.Services;
using Ghuboon.Core.Abstractions;

namespace Ghuboon.Tests.App.Settings;

public class PatValidationServiceTests
{
    [Fact]
    public async Task EmptyInput_IsRejectedWithoutCallingApi()
    {
        var api = new FakeGitHubApiClient();
        var svc = new PatValidationService(api);

        var result = await svc.ValidateAsync("");

        Assert.Equal(PatValidationStatus.Invalid, result.Status);
        Assert.Empty(api.ValidateInvocations);
    }

    [Fact]
    public async Task ShortInput_IsRejectedWithoutCallingApi()
    {
        var api = new FakeGitHubApiClient();
        var svc = new PatValidationService(api);

        var result = await svc.ValidateAsync("abc");

        Assert.Equal(PatValidationStatus.Invalid, result.Status);
        Assert.Empty(api.ValidateInvocations);
    }

    [Fact]
    public async Task TokenWithWhitespace_IsRejectedWithoutCallingApi()
    {
        var api = new FakeGitHubApiClient();
        var svc = new PatValidationService(api);

        var result = await svc.ValidateAsync("ghp_AAAAAAAAA AAAAAAAA0000000000000000");

        Assert.Equal(PatValidationStatus.Invalid, result.Status);
        Assert.Empty(api.ValidateInvocations);
    }

    [Fact]
    public async Task ApiThrowingException_IsMappedToNetworkError()
    {
        var api = new ThrowingApi();
        var svc = new PatValidationService(api);

        var result = await svc.ValidateAsync("ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA00000");

        Assert.Equal(PatValidationStatus.Invalid, result.Status);
        Assert.Equal(ErrorCategory.Network, result.ErrorCategory);
    }

    [Fact]
    public async Task SuccessfulValidation_IncludesLogin()
    {
        var api = new FakeGitHubApiClient
        {
            ValidateImpl = _ => new UserValidationResult(true, "octocat", null, null),
        };
        var svc = new PatValidationService(api);

        var result = await svc.ValidateAsync("ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA00000");

        Assert.Equal(PatValidationStatus.Valid, result.Status);
        Assert.Equal("octocat", result.Login);
    }

    private sealed class ThrowingApi : IGitHubApiClient
    {
        public Task<UserValidationResult> ValidateAsync(string pat, CancellationToken ct = default)
            => throw new InvalidOperationException("network down");

        public Task<NotificationsResponse> ListNotificationsAsync(string pat, NotificationsRequest request, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task MarkThreadReadAsync(string pat, string threadId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string?> GetSubjectBodyAsync(string pat, string subjectApiUrl, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string?> GetThreadSubjectUrlAsync(string pat, string threadId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
