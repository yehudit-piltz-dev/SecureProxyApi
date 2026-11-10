using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace SecureProxy.Tests.Infrastructure;

/// <summary>
/// Stands in for every destination API. It records each request it receives, so a test can prove
/// that a rejected request never reached the destination, rather than relying on the status code alone.
/// </summary>
public sealed class RecordingUpstreamHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> requests = new();

    /// <summary>Decides the upstream response. Tests replace it to simulate errors, delays and failures.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; } = DefaultResponder;

    public IReadOnlyList<RecordedRequest> Requests => requests.ToArray();

    public void Reset()
    {
        requests.Clear();
        Responder = DefaultResponder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, headers, body));
        return await Responder(request, cancellationToken);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Task<HttpResponseMessage> DefaultResponder(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Json(HttpStatusCode.OK, """{"source":"upstream","ok":true}"""));
}

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);
