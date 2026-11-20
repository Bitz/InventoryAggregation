using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InventoryAggregation.Core.Aggregation;
using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Records;

namespace InventoryAggregation.Core.Reporting;

/// <summary>
/// The lineage of one run, read as an ETL job (design.md D26): what was extracted, every correction the
/// transform made, and what was loaded into the figures, plus a fingerprint of the data itself.
/// </summary>
public sealed record RunAudit(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Fingerprint,
    ExtractAudit Extract,
    TransformAudit Transform,
    LoadAudit Load);

public sealed record ExtractAudit(
    int ReportedTotalRecords,
    int ReportedTotalPages,
    int RecordsReceived,
    int DistinctRecords,
    int Requests,
    int CollectionAttempts,
    IReadOnlyList<PageRefetch> PageRefetches,
    IReadOnlyList<string> Recollections,
    IReadOnlyList<CollapsedDuplicate> CollapsedDuplicates);

public sealed record TransformAudit(
    IReadOnlyList<CategorySpellings> Categories,
    TimestampCorrections Timestamps,
    IReadOnlyList<RejectedRecord> Rejected,
    IReadOnlyList<CategoryExclusion> ZeroQuantityExcluded);

/// <summary>The raw spellings that were folded into one category name.</summary>
public sealed record CategorySpellings(string Category, IReadOnlyList<SpellingCount> Spellings);

public sealed record SpellingCount(string Spelling, int Records);

public sealed record TimestampCorrections(IReadOnlyList<long> ConvertedToUtc, IReadOnlyList<long> AssumedUtc);

public sealed record CategoryExclusion(string Category, IReadOnlyList<long> Ids);

public sealed record LoadAudit(
    IReadOnlyList<CategoryLoad> Categories,
    int RecordsIn,
    int Rejected,
    int ZeroQuantityExcluded,
    int RecordsOut);

/// <summary>Records that reached a category, how many were excluded, and how many feed its figures.</summary>
public sealed record CategoryLoad(string Category, int RecordsIn, int ZeroQuantityExcluded, int RecordsOut);

/// <summary>Builds the audit from what the run produced. Pure: no I/O, no clock.</summary>
public static class RunAuditBuilder
{
    public static RunAudit Build(
        string runId,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        CollectedDataset dataset,
        ValidationResult validation,
        AggregationResult aggregation)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(aggregation);

        var shown = aggregation.Categories.ToDictionary(c => c.Category, c => c.Category, StringComparer.OrdinalIgnoreCase);
        var groups = validation.Accepted
            .GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: shown.GetValueOrDefault(g.Key) ?? CategoryAggregator.DisplayName(g.Select(r => r.Category)), Records: g.ToList()))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();

        var spellings = groups
            .Select(g => new CategorySpellings(
                g.Name,
                [.. g.Records
                    .GroupBy(r => r.RawCategory ?? r.Category, StringComparer.Ordinal)
                    .OrderByDescending(s => s.Count())
                    .ThenBy(s => s.Key, StringComparer.Ordinal)
                    .Select(s => new SpellingCount(s.Key, s.Count()))]))
            .ToList();

        var exclusions = groups
            .Select(g => new CategoryExclusion(g.Name, [.. g.Records.Where(r => r.Quantity == 0).Select(r => r.Id).Order()]))
            .Where(e => e.Ids.Count > 0)
            .ToList();

        var loads = groups
            .Select(g =>
            {
                var zero = g.Records.Count(r => r.Quantity == 0);
                return new CategoryLoad(g.Name, g.Records.Count, zero, g.Records.Count - zero);
            })
            .ToList();

        var lineage = dataset.Lineage;
        return new RunAudit(
            runId,
            startedAt,
            finishedAt,
            Fingerprint(dataset.Records),
            new ExtractAudit(
                dataset.TotalRecords,
                lineage.ReportedTotalPages,
                lineage.RecordsReceived,
                dataset.Records.Count,
                dataset.Requests,
                dataset.CollectionAttempts,
                lineage.PageRefetches,
                lineage.Recollections,
                lineage.CollapsedDuplicates),
            new TransformAudit(
                spellings,
                new TimestampCorrections(validation.ConvertedToUtcIds, validation.AssumedUtcIds),
                validation.Rejected,
                exclusions),
            new LoadAudit(
                loads,
                loads.Sum(l => l.RecordsIn),
                validation.Rejected.Count,
                loads.Sum(l => l.ZeroQuantityExcluded),
                loads.Sum(l => l.RecordsOut)));
    }

    /// <summary>Records accepted under a spelling other than the name they are shown under.</summary>
    public static int NormalizedSpellings(RunAudit audit) =>
        audit?.Transform.Categories.Sum(c => c.Spellings.Where(s => s.Spelling != c.Category).Sum(s => s.Records)) ?? 0;

    /// <summary>
    /// SHA-256 over the distinct records exactly as received, ordered by id. The same data gives the same
    /// value on every run; any change to any field of any record gives another.
    /// </summary>
    public static string Fingerprint(IEnumerable<RawRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var lines = records
            .Select(Canonical)
            .OrderBy(line => line, StringComparer.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    // Fields are separated by the unit separator so no field value can imitate a boundary. The id
    // comes first, zero-padded, so ordinal order is id order.
    private static string Canonical(RawRecord r) => string.Join(
        '\u001f',
        r.Id?.ToString("D20", CultureInfo.InvariantCulture) ?? "~",
        r.Name ?? "\u0000",
        r.Category ?? "\u0000",
        r.Price?.ToString(CultureInfo.InvariantCulture) ?? "\u0000",
        r.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "\u0000",
        r.LastUpdated ?? "\u0000",
        string.Join(',', r.MalformedFields.Order(StringComparer.Ordinal)));
}
