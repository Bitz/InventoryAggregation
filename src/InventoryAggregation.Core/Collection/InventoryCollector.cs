using System.Diagnostics.CodeAnalysis;
using InventoryAggregation.Core.Records;
using Microsoft.Extensions.Logging;

namespace InventoryAggregation.Core.Collection;

/// <summary>A dataset the collector has proven complete, with the evidence.</summary>
/// <param name="Records">One record per distinct id (plus any records without an id), in page order.</param>
/// <param name="TotalRecords">The total every page agreed on.</param>
/// <param name="PagesFetched">Data pages in the dataset (the end check is not counted).</param>
/// <param name="Requests">Requests sent to the upstream, retries, refetches and re-collection included.</param>
/// <param name="PageRefetches">Pages fetched again because they failed a check or did not parse.</param>
/// <param name="CollectionAttempts">Full passes over the upstream (1 when nothing needed settling).</param>
/// <param name="CollapsedDuplicateIds">Ids that arrived more than once with identical content.</param>
public sealed record CollectedDataset(
    IReadOnlyList<RawRecord> Records,
    int TotalRecords,
    int PagesFetched,
    int Requests,
    int PageRefetches,
    int CollectionAttempts,
    IReadOnlyList<long> CollapsedDuplicateIds);

/// <summary>A verified dataset or the reason there is none. Never a partial dataset (design.md D7).</summary>
public sealed class CollectionResult
{
    private CollectionResult(CollectedDataset? dataset, InventoryFailure? failure, int requests)
    {
        Dataset = dataset;
        Failure = failure;
        Requests = requests;
    }

    public CollectedDataset? Dataset { get; }

    public InventoryFailure? Failure { get; }

    /// <summary>Requests sent to the upstream, whatever the outcome.</summary>
    public int Requests { get; }

    [MemberNotNullWhen(true, nameof(Dataset))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSuccess => Dataset is not null;

    public static CollectionResult Success(CollectedDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return new(dataset, null, dataset.Requests);
    }

    public static CollectionResult Failed(InventoryFailure failure, int requests) => new(null, failure, requests);
}

/// <summary>
/// Walks every page the upstream reports and proves the result complete and consistent before anyone
/// computes a figure from it (design.md §3). Knows pages and totals, not HTTP.
/// </summary>
public sealed partial class InventoryCollector(
    IInventoryPageSource source,
    InventoryReportSettings settings,
    TimeProvider timeProvider,
    ILogger<InventoryCollector> logger)
{
    public async Task<CollectionResult> CollectAsync(CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(settings.CollectionTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var counters = new Counters();

        try
        {
            InventoryFailure? reconciliationFailure = null;
            for (var attempt = 1; attempt <= settings.CollectionAttempts; attempt++)
            {
                counters.Attempts = attempt;
                var pass = await CollectOnceAsync(counters, linked.Token).ConfigureAwait(false);

                if (pass.Dataset is { } dataset)
                {
                    LogCollected(dataset.Records.Count, dataset.PagesFetched, dataset.Requests, dataset.PageRefetches, dataset.CollectionAttempts);
                    return CollectionResult.Success(dataset);
                }

                var failure = pass.Failure!;
                if (failure.Kind is not (FailureKinds.IncompleteDataset or FailureKinds.ConflictingRecords))
                {
                    // Page-level failures have already had their retries and refetches.
                    LogFailed(failure.Kind, failure.Detail);
                    return CollectionResult.Failed(failure, counters.Requests);
                }

                // The dataset is static, so a second pass separates a glitch from a fault (design.md D20).
                reconciliationFailure = failure;
                if (attempt < settings.CollectionAttempts)
                {
                    LogRecollecting(attempt, failure.Detail);
                }
            }

            var final = reconciliationFailure! with
            {
                Detail = $"{reconciliationFailure!.Detail} after {settings.CollectionAttempts} collection attempt(s).",
            };
            LogFailed(final.Kind, final.Detail);
            return CollectionResult.Failed(final, counters.Requests);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var failure = new InventoryFailure(
                FailureKinds.UpstreamTimeout,
                $"The collection did not finish within {settings.CollectionTimeout.TotalSeconds:0.#} s.");
            LogFailed(failure.Kind, failure.Detail);
            return CollectionResult.Failed(failure, counters.Requests);
        }
    }

    private async Task<(CollectedDataset? Dataset, InventoryFailure? Failure)> CollectOnceAsync(Counters counters, CancellationToken cancellationToken)
    {
        // 1. Page 1 alone; its totals become the reference.
        var first = await FetchVerifiedAsync(1, reference: null, counters, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return (null, first.Failure);
        }

        var reference = first.Page!;
        var totalPages = reference.TotalPages;
        var pages = new InventoryPage[Math.Max(totalPages, 1)];
        pages[0] = reference;

        // 2-3. The rest, at most MaxConcurrency in flight, each checked and refetched as needed.
        if (totalPages > 1)
        {
            var failure = await FetchRemainingAsync(reference, pages, counters, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return (null, failure);
            }
        }

        // 4. Confirm the end of the data.
        var endFailure = totalPages == 0
            ? CheckEndPage(reference, page: 1)
            : await ConfirmEndAsync(totalPages + 1, counters, cancellationToken).ConfigureAwait(false);
        if (endFailure is not null)
        {
            return (null, endFailure);
        }

        // 5. Reconcile.
        return Reconcile(pages, reference.TotalRecords, counters);
    }

    private async Task<InventoryFailure?> FetchRemainingAsync(
        InventoryPage reference,
        InventoryPage[] pages,
        Counters counters,
        CancellationToken cancellationToken)
    {
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        InventoryFailure? failure = null;
        var next = 1;

        async Task Worker()
        {
            while (true)
            {
                var page = Interlocked.Increment(ref next);
                if (page > reference.TotalPages)
                {
                    return;
                }

                var result = await FetchVerifiedAsync(page, reference, counters, batch.Token).ConfigureAwait(false);
                if (result.Failure is not null)
                {
                    // First failure wins; stop the others, keep nothing (design.md D7).
                    Interlocked.CompareExchange(ref failure, result.Failure, null);
                    await batch.CancelAsync().ConfigureAwait(false);
                    return;
                }

                pages[page - 1] = result.Page!;
            }
        }

        var workerCount = Math.Min(settings.MaxConcurrency, reference.TotalPages - 1);
        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = Worker();
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref failure) is not null && !cancellationToken.IsCancellationRequested)
        {
            // The other workers were stopped because one page failed; that failure is the answer.
        }

        return Volatile.Read(ref failure);
    }

    /// <summary>Fetches one data page and checks it, fetching it again when it disagrees or does not parse.</summary>
    private async Task<(InventoryPage? Page, InventoryFailure? Failure)> FetchVerifiedAsync(
        int page,
        InventoryPage? reference,
        Counters counters,
        CancellationToken cancellationToken)
    {
        var fetches = 1 + settings.PageRefetchLimit;
        string? lastProblem = null;

        for (var fetch = 1; fetch <= fetches; fetch++)
        {
            if (fetch > 1)
            {
                counters.AddRefetch();
                LogRefetching(page, fetch - 1, lastProblem!);
            }

            var result = await source.GetPageAsync(page, settings.PageSize, cancellationToken).ConfigureAwait(false);
            counters.AddRequests(result.Attempts);

            if (!result.IsSuccess)
            {
                var failure = result.Failure with { Page = result.Failure.Page ?? page };
                if (failure.Kind == FailureKinds.MalformedResponse && fetch < fetches)
                {
                    lastProblem = failure.Detail;
                    continue;
                }

                return (null, failure.Kind == FailureKinds.MalformedResponse
                    ? failure with { Detail = $"{failure.Detail} ({fetch} fetch(es))" }
                    : failure);
            }

            // Absurd totals stop collection before anything else is fetched (design.md §3, MaxPages).
            if (page == 1 && result.Page.TotalPages > settings.MaxPages)
            {
                return (null, new InventoryFailure(
                    FailureKinds.DatasetTooLarge,
                    $"Page 1 reports {result.Page.TotalPages} pages; the limit is {settings.MaxPages}.")
                {
                    Page = 1,
                });
            }

            var problem = CheckPage(result.Page, page, reference ?? result.Page);
            if (problem is null)
            {
                return (result.Page, null);
            }

            lastProblem = problem;
        }

        return (null, new InventoryFailure(
            FailureKinds.InconsistentPaging,
            $"Page {page}: {lastProblem} ({fetches} fetch(es)).")
        {
            Page = page,
        });
    }

    /// <summary>Checks a data page against the reference. Returns the first problem found, or null.</summary>
    private string? CheckPage(InventoryPage body, int requested, InventoryPage reference)
    {
        var pageSize = settings.PageSize;

        if (body.Page != requested)
        {
            return $"page is {body.Page}, expected {requested}";
        }

        if (body.PageSize != pageSize)
        {
            return $"pageSize is {body.PageSize}, expected {pageSize}";
        }

        if (body.TotalRecords != reference.TotalRecords)
        {
            return $"totalRecords is {body.TotalRecords}, expected {reference.TotalRecords}";
        }

        if (body.TotalPages != reference.TotalPages)
        {
            return $"totalPages is {body.TotalPages}, expected {reference.TotalPages}";
        }

        if (body.TotalRecords < 0)
        {
            return $"totalRecords is {body.TotalRecords}";
        }

        var impliedPages = (int)Math.Ceiling(body.TotalRecords / (double)pageSize);
        var pagesAgree = body.TotalRecords == 0
            ? body.TotalPages is 0 or 1
            : body.TotalPages == impliedPages;
        if (!pagesAgree)
        {
            return $"totalPages is {body.TotalPages}, but {body.TotalRecords} records at {pageSize} per page need {impliedPages}";
        }

        // More records than expected is allowed: the upstream repeats records across page boundaries,
        // and reconciliation judges the extras (design.md D21). Fewer is a fault.
        var expected = ExpectedRecordCount(requested, reference);
        if (body.Records.Count < expected)
        {
            return $"holds {body.Records.Count} records, expected at least {expected}";
        }

        return null;
    }

    private int ExpectedRecordCount(int page, InventoryPage reference)
    {
        if (reference.TotalRecords == 0)
        {
            return 0;
        }

        return page < reference.TotalPages
            ? settings.PageSize
            : reference.TotalRecords - ((reference.TotalPages - 1) * settings.PageSize);
    }

    private async Task<InventoryFailure?> ConfirmEndAsync(int page, Counters counters, CancellationToken cancellationToken)
    {
        var fetches = 1 + settings.PageRefetchLimit;
        for (var fetch = 1; ; fetch++)
        {
            if (fetch > 1)
            {
                counters.AddRefetch();
            }

            var result = await source.GetPageAsync(page, settings.PageSize, cancellationToken).ConfigureAwait(false);
            counters.AddRequests(result.Attempts);

            if (result.IsSuccess)
            {
                return CheckEndPage(result.Page, page);
            }

            var failure = result.Failure with { Page = result.Failure.Page ?? page };

            // A 400 past the last page is the other accepted way to say "no more data" (design.md D8).
            if (failure is { Kind: FailureKinds.UpstreamMisconfigured, StatusCode: 400 })
            {
                return null;
            }

            if (failure.Kind == FailureKinds.MalformedResponse && fetch < fetches)
            {
                continue;
            }

            return failure;
        }
    }

    private static InventoryFailure? CheckEndPage(InventoryPage body, int page) =>
        body.Records.Count == 0
            ? null
            : new InventoryFailure(
                FailureKinds.InconsistentPaging,
                $"Page {page} is past the last reported page but holds {body.Records.Count} record(s); the totals understate the data.")
            {
                Page = page,
            };

    private static (CollectedDataset? Dataset, InventoryFailure? Failure) Reconcile(
        InventoryPage[] pages,
        int totalRecords,
        Counters counters)
    {
        var byId = new Dictionary<long, RawRecord>();
        var records = new List<RawRecord>();
        var duplicates = new SortedSet<long>();
        var conflicts = new SortedSet<long>();
        var withoutId = 0;

        foreach (var page in pages)
        {
            foreach (var record in page.Records)
            {
                if (record.Id is not { } id)
                {
                    // No identity to compare on; it counts as its own record and validation rejects it.
                    withoutId++;
                    records.Add(record);
                    continue;
                }

                if (byId.TryGetValue(id, out var existing))
                {
                    if (existing.Equals(record))
                    {
                        duplicates.Add(id);
                    }
                    else
                    {
                        conflicts.Add(id);
                    }

                    continue;
                }

                byId.Add(id, record);
                records.Add(record);
            }
        }

        var collected = byId.Count + withoutId;

        if (conflicts.Count > 0)
        {
            return (null, new InventoryFailure(
                FailureKinds.ConflictingRecords,
                $"{conflicts.Count} id(s) arrived with different content: {string.Join(", ", conflicts)}")
            {
                ExpectedCount = totalRecords,
                CollectedCount = collected,
                DuplicateIds = [.. duplicates],
                ConflictingIds = [.. conflicts],
            });
        }

        if (collected != totalRecords)
        {
            return (null, new InventoryFailure(
                FailureKinds.IncompleteDataset,
                $"Expected {totalRecords} distinct records, collected {collected}")
            {
                ExpectedCount = totalRecords,
                CollectedCount = collected,
                DuplicateIds = [.. duplicates],
            });
        }

        return (new CollectedDataset(
            records,
            totalRecords,
            PagesFetched: pages.Length,
            counters.Requests,
            counters.PageRefetches,
            counters.Attempts,
            [.. duplicates]), null);
    }

    private sealed class Counters
    {
        private int _requests;
        private int _pageRefetches;

        public int Requests => Volatile.Read(ref _requests);

        public int PageRefetches => Volatile.Read(ref _pageRefetches);

        public int Attempts { get; set; }

        public void AddRequests(int count) => Interlocked.Add(ref _requests, count);

        public void AddRefetch() => Interlocked.Increment(ref _pageRefetches);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Collected {Records} records over {Pages} pages in {Requests} requests ({Refetches} page refetches, {Attempts} collection attempts)")]
    private partial void LogCollected(int records, int pages, int requests, int refetches, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fetching page {Page} again (refetch {Refetch}): {Problem}")]
    private partial void LogRefetching(int page, int refetch, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collection attempt {Attempt} did not reconcile, collecting again: {Detail}")]
    private partial void LogRecollecting(int attempt, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collection failed ({Kind}): {Detail}")]
    private partial void LogFailed(string kind, string detail);
}
