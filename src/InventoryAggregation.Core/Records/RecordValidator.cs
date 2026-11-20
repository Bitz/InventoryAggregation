using System.Globalization;
using System.Text.RegularExpressions;

namespace InventoryAggregation.Core.Records;

/// <summary>
/// Decides which raw records may feed a figure, and normalizes the ones that may. Knows nothing about
/// where the records came from: no HTTP, no JSON, no configuration.
/// </summary>
public sealed partial class RecordValidator
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Registered and injected as a component.")]
    public ValidationResult Validate(IEnumerable<RawRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var accepted = new List<InventoryRecord>();
        var rejected = new List<RejectedRecord>();
        var assumedUtc = 0;

        foreach (var record in records)
        {
            var reasons = new List<string>();

            var id = CheckId(record, reasons);
            var name = CheckText(record.Name, RecordFields.Name, record, reasons);
            var category = CheckText(record.Category, RecordFields.Category, record, reasons);
            var price = CheckPrice(record, reasons);
            var quantity = CheckQuantity(record, reasons);
            var lastUpdated = CheckTimestamp(record, reasons, out var offsetAssumed);

            if (reasons.Count > 0)
            {
                rejected.Add(new RejectedRecord(record.Id, reasons));
                continue;
            }

            if (offsetAssumed)
            {
                assumedUtc++;
            }

            accepted.Add(new InventoryRecord(
                id!.Value,
                name!,
                category!.Trim(),
                price!.Value,
                quantity!.Value,
                lastUpdated!.Value));
        }

        return new ValidationResult(accepted, rejected, assumedUtc);
    }

    private static long? CheckId(RawRecord record, List<string> reasons)
    {
        if (IsMalformed(record, RecordFields.Id))
        {
            reasons.Add("id is not a whole number");
            return null;
        }

        switch (record.Id)
        {
            case null:
                reasons.Add("id is missing");
                return null;
            case <= 0:
                reasons.Add("id is not positive");
                return null;
            default:
                return record.Id;
        }
    }

    private static string? CheckText(string? value, string field, RawRecord record, List<string> reasons)
    {
        if (IsMalformed(record, field))
        {
            reasons.Add($"{field} is not a string");
            return null;
        }

        if (value is null)
        {
            reasons.Add($"{field} is missing");
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            reasons.Add($"{field} is blank");
            return null;
        }

        return value;
    }

    private static decimal? CheckPrice(RawRecord record, List<string> reasons)
    {
        if (IsMalformed(record, RecordFields.Price))
        {
            reasons.Add("price is not a number");
            return null;
        }

        switch (record.Price)
        {
            case null:
                reasons.Add("price is missing");
                return null;
            case < 0:
                reasons.Add("price is negative");
                return null;
            default:
                return record.Price;
        }
    }

    private static long? CheckQuantity(RawRecord record, List<string> reasons)
    {
        if (IsMalformed(record, RecordFields.Quantity))
        {
            reasons.Add("quantity is not a number");
            return null;
        }

        if (record.Quantity is not { } quantity)
        {
            reasons.Add("quantity is missing");
            return null;
        }

        var ok = true;
        if (decimal.Truncate(quantity) != quantity)
        {
            reasons.Add("quantity is not a whole number");
            ok = false;
        }

        if (quantity < 0)
        {
            reasons.Add("quantity is negative");
            ok = false;
        }
        else if (quantity > long.MaxValue)
        {
            reasons.Add("quantity is too large");
            ok = false;
        }

        return ok ? (long)quantity : null;
    }

    private static DateTimeOffset? CheckTimestamp(RawRecord record, List<string> reasons, out bool offsetAssumed)
    {
        offsetAssumed = false;

        if (IsMalformed(record, RecordFields.LastUpdated))
        {
            reasons.Add("lastUpdated is not a string");
            return null;
        }

        if (record.LastUpdated is not { } text)
        {
            reasons.Add("lastUpdated is missing");
            return null;
        }

        if (!TryParseTimestamp(text, out var instant, out offsetAssumed))
        {
            reasons.Add("lastUpdated is not an ISO 8601 timestamp");
            return null;
        }

        return instant;
    }

    /// <summary>
    /// Parses an ISO 8601 date and time, with or without an offset. A value without an offset is read as
    /// UTC, as the upstream contract says the field is UTC (design.md D14). The result is always UTC.
    /// </summary>
    internal static bool TryParseTimestamp(string text, out DateTimeOffset instant, out bool offsetAssumed)
    {
        instant = default;
        offsetAssumed = false;

        var match = IsoTimestamp().Match(text);
        if (!match.Success)
        {
            return false;
        }

        offsetAssumed = !match.Groups["offset"].Success;
        var styles = offsetAssumed ? DateTimeStyles.AssumeUniversal : DateTimeStyles.None;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out var parsed))
        {
            return false;
        }

        instant = parsed.ToUniversalTime();
        return true;
    }

    private static bool IsMalformed(RawRecord record, string field) => record.MalformedFields.Contains(field);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(?<offset>Z|[+-]\d{2}:\d{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTimestamp();
}
