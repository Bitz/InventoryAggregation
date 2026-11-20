using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Web;

namespace InventoryAggregation.Api.Tests.Support;

/// <summary>
/// JSON pages shaped like the real upstream's: 157 records from id 1001, padded mixed-case categories,
/// 21 zero-quantity records, and every even-numbered page repeating the previous page's last record.
/// Optionally enforces a fixed-window rate limit the way the upstream does, with window boundaries the
/// client cannot see.
/// </summary>
internal sealed class FakeUpstream
{
    private static readonly string[][] Spellings =
    [
        ["Electronics", " electronics", "ELECTRONICS "],
        ["Toys", "  toys ", "TOYS"],
        ["Office Supplies", "office supplies ", " OFFICE SUPPLIES"],
    ];

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly int? _limit;
    private readonly TimeSpan _window;
    private readonly DateTimeOffset _epoch;
    private long _currentWindow = -1;
    private int _inWindow;

    public FakeUpstream(TimeProvider time, int? limitPerWindow = null, TimeSpan? window = null, TimeSpan? windowOffset = null)
    {
        _time = time;
        _limit = limitPerWindow;
        _window = window ?? TimeSpan.FromSeconds(10);

        // The upstream's windows start wherever its clock says, not when our first request arrives.
        _epoch = time.GetUtcNow() - (windowOffset ?? TimeSpan.FromSeconds(7.3));
        Records = [.. Enumerable.Range(1001, 157).Select(Record)];
    }

    public List<Dictionary<string, object?>> Records { get; }

    public int Throttled { get; private set; }

    public static string ApiKeyHeader => "X-Api-Key";

    public HttpResponseMessage Respond(HttpRequestMessage request)
    {
        if (_limit is { } limit)
        {
            lock (_gate)
            {
                var window = (long)((_time.GetUtcNow() - _epoch) / _window);
                if (window != _currentWindow)
                {
                    _currentWindow = window;
                    _inWindow = 0;
                }

                if (++_inWindow > limit)
                {
                    Throttled++;
                    return ScriptedHttpHandler.Problem(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1);
                }
            }
        }

        var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var page = int.Parse(query["page"]!, CultureInfo.InvariantCulture);
        var size = int.Parse(query["pageSize"]!, CultureInfo.InvariantCulture);
        return ScriptedHttpHandler.Json(PageJson(page, size));
    }

    public string PageJson(int page, int size, Func<List<Dictionary<string, object?>>, List<Dictionary<string, object?>>>? edit = null)
    {
        var records = Records.Skip((page - 1) * size).Take(size).ToList();
        if (page % 2 == 0 && records.Count > 0)
        {
            records.Insert(0, Records[((page - 1) * size) - 1]);
        }

        if (edit is not null)
        {
            records = edit(records);
        }

        return JsonSerializer.Serialize(new
        {
            records,
            page,
            pageSize = size,
            totalPages = (int)Math.Ceiling(Records.Count / (double)size),
            totalRecords = Records.Count,
        });
    }

    private static Dictionary<string, object?> Record(int id)
    {
        var spellings = Spellings[id % Spellings.Length];
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = $"Item {id}",
            ["category"] = spellings[id % 7 % spellings.Length],
            ["price"] = Math.Round(1.25m + (id % 13 * 3.1m), 2),
            ["quantity"] = id % 8 == 0 ? 0 : id % 9 + 1,
            ["lastUpdated"] = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(id * 37 % 10_000).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
    }
}
