using System.Diagnostics.CodeAnalysis;
using InventoryAggregation.Core.Records;

namespace InventoryAggregation.Core.Collection;

/// <summary>
/// "Give me page <i>n</i>." The only way Core reaches the upstream. An implementation returns a parsed
/// page or a classified failure and does not throw for expected failures; cancellation requested by
/// the caller surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public interface IInventoryPageSource
{
    Task<PageResult> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken);
}

/// <summary>One page envelope as the upstream returned it. Nothing in it has been checked yet.</summary>
public sealed record InventoryPage(
    int Page,
    int PageSize,
    int TotalPages,
    int TotalRecords,
    IReadOnlyList<RawRecord> Records);

/// <summary>A page, or the reason there is none, plus the number of requests it took.</summary>
public sealed class PageResult
{
    private PageResult(InventoryPage? page, InventoryFailure? failure, int attempts)
    {
        Page = page;
        Failure = failure;
        Attempts = attempts;
    }

    public InventoryPage? Page { get; }

    public InventoryFailure? Failure { get; }

    /// <summary>Requests sent for this page, retries included.</summary>
    public int Attempts { get; }

    [MemberNotNullWhen(true, nameof(Page))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSuccess => Page is not null;

    public static PageResult Success(InventoryPage page, int attempts) => new(page, null, attempts);

    public static PageResult Failed(InventoryFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(null, failure, failure.Attempts);
    }
}
