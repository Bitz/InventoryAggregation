// A local stand-in for the upstream Inventory API. It serves a generated, static dataset on
// GET /api/records and misbehaves in one chosen way per run, so anyone can reproduce an upstream
// behaviour and see which failure kind the service names.
//
//   dotnet run --project tools/FakeInventoryApi -- --mode <mode>
//
// Every mode keeps the observed upstream habits: 5 requests per 10 s (fixed window, 429 with
// Retry-After beyond it), even-numbered pages repeating the previous page's last record, and padded
// mixed-case category names. Each request's headers are printed; the API key value is not.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

var modes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["healthy"] = "behaves as observed: a 200 report",
    ["flaky-500"] = "the first request for each page answers 500, the retry succeeds: a 200 report",
    ["throttle"] = "every request answers 429 with Retry-After: 120: 503 upstream-throttled",
    ["hang"] = "page 2 never answers: 502 upstream-unavailable after the page timeout",
    ["drift-totals"] = "page 5 reports totalRecords 160: 502 inconsistent-paging",
    ["overlap"] = "page 3 repeats a record and omits one: 502 incomplete-dataset",
    ["short-page"] = "page 3 holds 24 records: 502 inconsistent-paging",
    ["wrong-page"] = "page 4 says it is page 3: 502 inconsistent-paging",
    ["conflict"] = "id 1042 appears again on page 4 with another quantity: 502 conflicting-records",
    ["bad-record"] = "id 1099 has a negative price: a 200 report listing it as rejected",
    ["truncated"] = "page 2's body ends mid-record: 502 malformed-response",
    ["extra-page"] = "the page after the last holds 3 records: 502 inconsistent-paging",
    ["unknown-status"] = "every request answers 418: 502 upstream-rejected",
};

var builder = WebApplication.CreateBuilder(args);
var mode = builder.Configuration["mode"] ?? "healthy";
if (!modes.TryGetValue(mode, out var effectOfMode))
{
    Console.Error.WriteLine($"Unknown mode '{mode}'. Modes:");
    foreach (var (name, effect) in modes)
    {
        Console.Error.WriteLine($"  {name,-15} {effect}");
    }

    return 1;
}

builder.Logging.ClearProviders();
var app = builder.Build();

const string KeyHeader = "X-Api-Key";
var records = Dataset.Create();
var limiter = new FixedWindow(5, TimeSpan.FromSeconds(10));
var seen = new ConcurrentDictionary<int, int>();
var requestNumber = 0;

app.MapGet("/api/records", async (HttpContext context) =>
{
    var number = Interlocked.Increment(ref requestNumber);
    PrintRequest(number, context.Request);

    if (string.IsNullOrEmpty(context.Request.Headers[KeyHeader]))
    {
        return Results.Problem(statusCode: 401, title: "Unauthorized", detail: $"A valid API key is required in the {KeyHeader} header.");
    }

    if (limiter.TryTake(out var retryAfter) is false)
    {
        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        return Results.Problem(statusCode: 429, title: "Too Many Requests", detail: "Rate limit of 5 requests per 10 seconds exceeded.");
    }

    if (!int.TryParse(context.Request.Query["page"], out var page) || page < 1)
    {
        return Results.Problem(statusCode: 400, title: "Bad Request", detail: "page must be an integer of 1 or greater.");
    }

    var sizeText = context.Request.Query["pageSize"].ToString();
    var size = sizeText.Length == 0 ? 20 : int.TryParse(sizeText, out var s) ? s : -1;
    if (size is < 1 or > 25)
    {
        return Results.Problem(statusCode: 400, title: "Bad Request", detail: "pageSize must be an integer from 1 to 25.");
    }

    var timesSeen = seen.AddOrUpdate(page, 1, (_, n) => n + 1);
    switch (mode)
    {
        case "throttle":
            context.Response.Headers.RetryAfter = "120";
            return Results.Problem(statusCode: 429, title: "Too Many Requests", detail: "Throttled.");
        case "unknown-status":
            return Results.Problem(statusCode: 418, title: "I'm a teapot");
        case "flaky-500" when timesSeen == 1:
            return Results.Problem(statusCode: 500, title: "Internal Server Error");
        case "hang" when page == 2:
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
            break;
    }

    var totalPages = (int)Math.Ceiling(records.Count / (double)size);
    var pageRecords = records.Skip((page - 1) * size).Take(size).ToList();
    if (page % 2 == 0 && page <= totalPages)
    {
        pageRecords.Insert(0, records[((page - 1) * size) - 1]);
    }

    var envelope = new Dictionary<string, object?>
    {
        ["page"] = page,
        ["pageSize"] = size,
        ["totalPages"] = totalPages,
        ["totalRecords"] = records.Count,
    };

    switch (mode)
    {
        case "drift-totals" when page == 5:
            envelope["totalRecords"] = 160;
            break;
        case "overlap" when page == 3:
            pageRecords = [records[(2 * size) - 1], .. pageRecords.Take(pageRecords.Count - 1)];
            break;
        case "short-page" when page == 3:
            pageRecords.RemoveAt(pageRecords.Count - 1);
            break;
        case "wrong-page" when page == 4:
            envelope["page"] = 3;
            break;
        case "conflict" when page == 4:
            var copy = new Dictionary<string, object?>(records.Single(r => (int)r["id"]! == 1042)) { ["quantity"] = 999 };
            pageRecords.Insert(0, copy);
            break;
        case "bad-record":
            pageRecords = [.. pageRecords.Select(r => (int)r["id"]! == 1099 ? new Dictionary<string, object?>(r) { ["price"] = -4.5m } : r)];
            break;
        case "extra-page" when page == totalPages + 1:
            pageRecords = [.. Enumerable.Range(2001, 3).Select(Dataset.Record)];
            break;
    }

    envelope["records"] = pageRecords;
    var json = JsonSerializer.Serialize(envelope);
    if (mode == "truncated" && page == 2)
    {
        json = json[..(json.Length / 2)];
    }

    return Results.Text(json, "application/json");
});

Console.WriteLine($"Fake Inventory API, mode '{mode}': {effectOfMode}");
await app.RunAsync();
return 0;

static void PrintRequest(int number, HttpRequest request)
{
    var lines = request.Headers
        .Select(h => h.Key.Equals(KeyHeader, StringComparison.OrdinalIgnoreCase)
            ? $"    {h.Key}: <redacted, {h.Value.ToString().Length} characters>"
            : $"    {h.Key}: {h.Value}");
    Console.WriteLine($"#{number} {request.Method} {request.Path}{request.QueryString}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
}

/// <summary>5 requests per 10 s in windows aligned to the wall clock, like the upstream.</summary>
internal sealed class FixedWindow(int limit, TimeSpan window)
{
    private readonly Lock _gate = new();
    private long _window = -1;
    private int _count;

    public bool TryTake(out int retryAfterSeconds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowMs = (long)window.TotalMilliseconds;
        lock (_gate)
        {
            var current = now / windowMs;
            if (current != _window)
            {
                _window = current;
                _count = 0;
            }

            retryAfterSeconds = (int)Math.Ceiling(((current + 1) * windowMs - now) / 1000.0);
            return ++_count <= limit;
        }
    }
}

/// <summary>157 records shaped like the upstream's: ids 1001-1157, six categories in six spellings, 21 out of stock.</summary>
internal static class Dataset
{
    private static readonly string[][] Spellings =
    [
        ["Electronics", " electronics", "ELECTRONICS ", "  Electronics", "electronics ", "Electronics"],
        ["Home & Kitchen", "home & kitchen ", " HOME & KITCHEN", "Home & Kitchen ", "  home & kitchen", "Home & Kitchen"],
        ["Office Supplies", " office supplies", "OFFICE SUPPLIES ", "Office Supplies ", "  office supplies ", "Office Supplies"],
        ["Outdoor", "outdoor ", " OUTDOOR", "Outdoor  ", " outdoor ", "Outdoor"],
        ["Toys", "  toys ", " Toys", "toys", "TOYS", "Toys "],
    ];

    public static List<Dictionary<string, object?>> Create() => [.. Enumerable.Range(1001, 157).Select(Record)];

    public static Dictionary<string, object?> Record(int id)
    {
        var discontinued = id >= 1151 && id <= 1157;
        var group = Spellings[id % Spellings.Length];
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = $"Item MK-{id}",
            ["category"] = discontinued ? (id % 2 == 0 ? "discontinued " : " Discontinued") : group[id / 5 % group.Length],
            ["price"] = Math.Round(5m + (id % 17 * 6.35m), 2),
            ["quantity"] = discontinued || id % 11 == 0 ? 0 : id % 23 + 1,
            ["lastUpdated"] = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(id * 97 % 40_000).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
    }
}
