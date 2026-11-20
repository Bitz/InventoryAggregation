using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace InventoryAggregation.Api.Tests.Support;

/// <summary>A primary HTTP handler driven by a script. Records every request it receives and when, in fake time.</summary>
internal sealed class ScriptedHttpHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> script, TimeProvider time)
    : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private int _count;

    public Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> Script { get; set; } = script;

    public ScriptedHttpHandler(Func<HttpRequestMessage, int, HttpResponseMessage> script, TimeProvider time)
        : this((request, n, _) => Task.FromResult(script(request, n)), time)
    {
    }

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _count) - 1;
        _requests.Enqueue(new RecordedRequest(
            request.RequestUri!,
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            time.GetUtcNow()));

        var response = await Script(request, n, cancellationToken);
        response.RequestMessage = request;
        return response;
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    public static HttpResponseMessage Problem(HttpStatusCode status, int? retryAfterSeconds = null) =>
        Json($$"""{"title":"error","status":{{(int)status}},"traceId":"00-upstream-trace-01"}""", status, retryAfterSeconds);
}

internal sealed record RecordedRequest(Uri Uri, IReadOnlyDictionary<string, string[]> Headers, DateTimeOffset At);
