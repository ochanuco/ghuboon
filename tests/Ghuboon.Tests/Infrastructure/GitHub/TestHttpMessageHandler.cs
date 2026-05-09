namespace Ghuboon.Tests.Infrastructure.GitHub;

/// <summary>
/// Func-based fake <see cref="HttpMessageHandler"/> for unit-testing the GitHub API client.
/// Captures the most recent request so tests can assert on headers without leaking secrets
/// to any shared sink.
/// </summary>
internal sealed class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public List<HttpRequestMessage> Requests { get; } = new();

    public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this((req, ct) => Task.FromResult(responder(req)))
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Snapshot the request — capture body content if any so tests can read it after dispose.
        Requests.Add(request);
        return _responder(request, cancellationToken);
    }
}
