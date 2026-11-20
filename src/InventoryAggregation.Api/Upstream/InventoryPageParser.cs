using System.Text.Json;
using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Records;

namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// Reads a <c>200</c> body into the page envelope without trusting it. The envelope must be complete;
/// each record is read field by field into nullable raw values, so a bad field in one record never
/// fails the page. Unknown fields are ignored and names match case-insensitively.
/// </summary>
internal static class InventoryPageParser
{
    public static bool TryParse(JsonElement root, out InventoryPage? page, out string? problem)
    {
        page = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            problem = $"the body is a JSON {root.ValueKind}, not an object";
            return false;
        }

        if (!TryGetInt(root, "page", out var number, out problem)
            || !TryGetInt(root, "pageSize", out var pageSize, out problem)
            || !TryGetInt(root, "totalPages", out var totalPages, out problem)
            || !TryGetInt(root, "totalRecords", out var totalRecords, out problem))
        {
            return false;
        }

        if (Property(root, "records") is not { ValueKind: JsonValueKind.Array } records)
        {
            problem = "the body has no records array";
            return false;
        }

        var raw = new List<RawRecord>(records.GetArrayLength());
        foreach (var element in records.EnumerateArray())
        {
            raw.Add(ReadRecord(element));
        }

        page = new InventoryPage(number, pageSize, totalPages, totalRecords, raw);
        problem = null;
        return true;
    }

    private static RawRecord ReadRecord(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new RawRecord();
        }

        var malformed = new List<string>();
        return new RawRecord
        {
            Id = ReadLong(element, RecordFields.Id, malformed),
            Name = ReadString(element, RecordFields.Name, malformed),
            Category = ReadString(element, RecordFields.Category, malformed),
            Price = ReadDecimal(element, RecordFields.Price, malformed),
            Quantity = ReadDecimal(element, RecordFields.Quantity, malformed),
            LastUpdated = ReadString(element, RecordFields.LastUpdated, malformed),
            MalformedFields = malformed,
        };
    }

    private static long? ReadLong(JsonElement record, string field, List<string> malformed)
    {
        switch (Property(record, field))
        {
            case null or { ValueKind: JsonValueKind.Null }:
                return null;
            case { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var number):
                return number;
            default:
                malformed.Add(field);
                return null;
        }
    }

    // TryGetDecimal reads the number's text directly into decimal; it never passes through double.
    private static decimal? ReadDecimal(JsonElement record, string field, List<string> malformed)
    {
        switch (Property(record, field))
        {
            case null or { ValueKind: JsonValueKind.Null }:
                return null;
            case { ValueKind: JsonValueKind.Number } value when value.TryGetDecimal(out var number):
                return number;
            default:
                malformed.Add(field);
                return null;
        }
    }

    private static string? ReadString(JsonElement record, string field, List<string> malformed)
    {
        switch (Property(record, field))
        {
            case null or { ValueKind: JsonValueKind.Null }:
                return null;
            case { ValueKind: JsonValueKind.String } value:
                return value.GetString();
            default:
                malformed.Add(field);
                return null;
        }
    }

    private static bool TryGetInt(JsonElement root, string name, out int value, out string? problem)
    {
        if (Property(root, name) is { ValueKind: JsonValueKind.Number } element && element.TryGetInt32(out value))
        {
            problem = null;
            return true;
        }

        value = 0;
        problem = $"the body has no whole-number {name}";
        return false;
    }

    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
