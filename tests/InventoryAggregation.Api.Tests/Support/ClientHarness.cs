using InventoryAggregation.Api.Upstream;
using InventoryAggregation.Core.Collection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace InventoryAggregation.Api.Tests.Support;

/// <summary>
/// The production registrations from <c>AddInventoryAggregation</c> with two substitutions: a fake
/// clock and a scripted primary HTTP handler. Everything between (typed client, resilience pipeline,
/// rate limiter, attempt handler) is the real thing.
/// </summary>
internal sealed class ClientHarness : IAsyncDisposable
{
    public const string ApiKey = "test-key-5b8c1e";
    public const string BaseUrl = "https://inventory.test/";

    public ClientHarness(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>>? script = null,
        IDictionary<string, string?>? settings = null,
        bool replacePrimaryHandler = true)
    {
        Handler = new ScriptedHttpHandler(script ?? ((_, _, _) => throw new InvalidOperationException("no script")), Time);

        var values = new Dictionary<string, string?>
        {
            ["InventoryApi:BaseUrl"] = BaseUrl,
            ["InventoryApi:ApiKey"] = ApiKey,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Time);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddFakeLogging());
        services.AddInventoryAggregation(configuration);
        if (replacePrimaryHandler)
        {
            services.AddHttpClient(InventoryApiClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() =>
            {
                Interlocked.Increment(ref _handlersCreated);
                return Handler;
            });
        }

        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private int _handlersCreated;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));

    public ScriptedHttpHandler Handler { get; }

    public ServiceProvider Services { get; }

    public int HandlersCreated => Volatile.Read(ref _handlersCreated);

    public FakeLogCollector Logs => Services.GetFakeLogCollector();

    public IInventoryPageSource Client() => Services.GetRequiredService<IInventoryPageSource>();

    /// <summary>
    /// Advances fake time in small steps until <paramref name="task"/> completes, so retries, backoff,
    /// timeouts and pacing run without real waiting.
    /// </summary>
    public Task<T> Drive<T>(Task<T> task, TimeSpan? step = null, TimeSpan? limit = null) =>
        FakeClock.Drive(Time, task, step, limit);

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

internal static class FakeClock
{
    public static async Task<T> Drive<T>(FakeTimeProvider time, Task<T> task, TimeSpan? step = null, TimeSpan? limit = null)
    {
        var increment = step ?? TimeSpan.FromMilliseconds(250);
        var budget = limit ?? TimeSpan.FromMinutes(5);
        var advanced = TimeSpan.Zero;

        while (!task.IsCompleted)
        {
            // Some continuations hop to the thread pool (cancellation callbacks, disposal of linked
            // registrations). A brief pause before each step lets them run, so the fake clock never
            // runs ahead of the code it is timing. No test waits for a simulated delay in real time:
            // a 30 s page timeout takes a few hundred milliseconds.
            await Task.Delay(TimeSpan.FromMilliseconds(1), CancellationToken.None);
            if (task.IsCompleted)
            {
                break;
            }

            if (advanced >= budget)
            {
                throw new TimeoutException($"Still running after {budget} of fake time.");
            }

            time.Advance(increment);
            advanced += increment;
        }

        return await task;
    }

    /// <summary>Advances fake time in small steps until <paramref name="condition"/> holds, as <see cref="Drive{T}"/> does for a task.</summary>
    public static async Task DriveUntil(FakeTimeProvider time, Func<bool> condition, TimeSpan? step = null, TimeSpan? limit = null)
    {
        var increment = step ?? TimeSpan.FromMilliseconds(250);
        var budget = limit ?? TimeSpan.FromMinutes(5);
        var advanced = TimeSpan.Zero;

        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), CancellationToken.None);
            if (condition())
            {
                break;
            }

            if (advanced >= budget)
            {
                throw new TimeoutException($"Condition still false after {budget} of fake time.");
            }

            time.Advance(increment);
            advanced += increment;
        }
    }

    /// <summary>Waits, without a timer, until a condition holds. Fails after 10 s of real time.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException($"Gave up waiting for: {what}");
            }

            await Task.Yield();
            Thread.Yield();
        }
    }
}
