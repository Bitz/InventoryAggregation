namespace InventoryAggregation.Api.Upstream;

/// <summary>What happened to one page request across its attempts. Carried on the request message.</summary>
internal sealed class UpstreamAttempts
{
    public static readonly HttpRequestOptionsKey<UpstreamAttempts> Key = new("InventoryAggregation.UpstreamAttempts");

    private int _count;

    public required int Page { get; init; }

    public int Count => Volatile.Read(ref _count);

    public int? LastStatus { get; private set; }

    public void Started() => Interlocked.Increment(ref _count);

    public void Answered(int status) => LastStatus = status;
}

/// <summary>
/// The innermost handler, below the resilience pipeline, so it sees every attempt. It counts attempts,
/// remembers the last status, and reads the body into memory while the attempt timeout still applies,
/// so a body that stalls or breaks off mid-stream is an attempt failure that can be retried.
/// </summary>
internal sealed class UpstreamAttemptHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.TryGetValue(UpstreamAttempts.Key, out var attempts);
        attempts?.Started();

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        attempts?.Answered((int)response.StatusCode);

        try
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
    }
}
