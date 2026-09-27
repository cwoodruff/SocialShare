using System.Net;
using System.Text;

namespace SocialShare.Tests.Support;

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, HttpRequestHeaders Headers)
{
    public bool UriEndsWith(string suffix) => Uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal);
}

public sealed record HttpRequestHeaders(string? Authorization, string? ContentType, IReadOnlyDictionary<string, string> All);

/// <summary>
/// A hand written HttpMessageHandler. A mocking framework would buy nothing here: the platform
/// code only ever needs canned responses matched on the URL, and doing it by hand keeps the
/// recorded requests available for assertions.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _rules = [];

    public List<RecordedRequest> Requests { get; } = [];

    public StubHttpMessageHandler RespondJson(string uriContains, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _rules.Add((
            request => request.RequestUri!.ToString().Contains(uriContains, StringComparison.OrdinalIgnoreCase),
            _ => new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }));

        return this;
    }

    public StubHttpMessageHandler RespondText(
        string uriContains, string body, HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        _rules.Add((
            request => request.RequestUri!.ToString().Contains(uriContains, StringComparison.OrdinalIgnoreCase),
            _ =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
                foreach (var (name, value) in headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }

                return response;
            }));

        return this;
    }

    /// <summary>Simulates the socket failing, rather than the server answering badly.</summary>
    public StubHttpMessageHandler Throw(string uriContains, Exception exception)
    {
        _rules.Add((
            request => request.RequestUri!.ToString().Contains(uriContains, StringComparison.OrdinalIgnoreCase),
            _ => throw exception));

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            body,
            new HttpRequestHeaders(
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.ToString(),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)))));

        foreach (var (match, respond) in _rules)
        {
            if (match(request))
            {
                return respond(request);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No stub rule matched {request.Method} {request.RequestUri}")
        };
    }

    public IHttpClientFactory AsFactory() => new StubFactory(this);

    private sealed class StubFactory(StubHttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
    }
}
