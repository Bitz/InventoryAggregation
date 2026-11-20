using System.Text.Json;
using InventoryAggregation.Api.Configuration;
using InventoryAggregation.Api.Tests.Support;
using InventoryAggregation.Core.Aggregation;
using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Records;
using InventoryAggregation.Core.Reporting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Tests.Configuration;

public sealed class ServiceConfigurationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "service-configuration/Missing key")]
    public async Task Startup_fails_naming_the_missing_key_and_how_to_set_it()
    {
        var error = await StartupFailureAsync(new() { ["InventoryApi:ApiKey"] = "" });

        var message = Assert.Single(error.Failures);
        Assert.Contains("InventoryApi:ApiKey", message, StringComparison.Ordinal);
        Assert.Contains("dotnet user-secrets set", message, StringComparison.Ordinal);
        Assert.Contains("InventoryApi__ApiKey", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "service-configuration/Page size out of range")]
    public async Task Startup_fails_naming_the_page_size_and_its_range()
    {
        var error = await StartupFailureAsync(new() { ["InventoryApi:PageSize"] = "50" });

        Assert.Contains("InventoryApi:PageSize is 50; it must be from 1 to 25.", error.Failures);
    }

    [Theory]
    [InlineData("InventoryApi:BaseUrl", "ftp://inventory.test/", "InventoryApi:BaseUrl must be an absolute http or https URI.")]
    [InlineData("InventoryApi:BaseUrl", "/relative", "InventoryApi:BaseUrl must be an absolute http or https URI.")]
    [InlineData("InventoryApi:ApiKeyHeader", "X Api Key", "InventoryApi:ApiKeyHeader must be a valid HTTP header name.")]
    [InlineData("InventoryApi:PageTimeout", "00:00:05", "InventoryApi:PageTimeout is 00:00:05; it must be at least InventoryApi:AttemptTimeout (00:00:10).")]
    [InlineData("InventoryApi:CollectionAttempts", "4", "InventoryApi:CollectionAttempts is 4; it must be from 1 to 3.")]
    [InlineData("InventoryApi:RateLimitWindow", "00:00:00", "InventoryApi:RateLimitWindow is 00:00:00; it must be greater than zero.")]
    public void Other_rules_name_their_setting(string setting, string value, string expected)
    {
        var options = new InventoryApiOptions { BaseUrl = ClientHarness.BaseUrl, ApiKey = "k" };
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [setting] = value })
            .Build();
        Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(configuration.GetSection("InventoryApi"), options);

        var result = new InventoryApiOptionsValidator().Validate(null, options);

        Assert.Contains(expected, result.Failures ?? []);
    }

    [Fact]
    public void The_defaults_pass_validation_and_the_options_never_print_secrets()
    {
        var options = new InventoryApiOptions { BaseUrl = ClientHarness.BaseUrl, ApiKey = ClientHarness.ApiKey };

        Assert.True(new InventoryApiOptionsValidator().Validate(null, options).Succeeded);
        Assert.DoesNotContain(ClientHarness.ApiKey, options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("inventory.test", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "service-configuration/Clean clone")]
    public void No_committed_configuration_holds_a_key_or_base_url()
    {
        var root = RepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "appsettings*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.TryGetProperty("InventoryApi", out var section))
            {
                Assert.False(section.TryGetProperty("ApiKey", out _), $"{file} sets InventoryApi:ApiKey");
                Assert.False(section.TryGetProperty("BaseUrl", out _), $"{file} sets InventoryApi:BaseUrl");
            }
        }

        var gitignore = File.ReadAllLines(Path.Combine(root, ".gitignore"));
        Assert.Contains("*.secrets.json", gitignore);
        Assert.Contains(".env", gitignore);
        Assert.Contains("recordings/", gitignore);

        var project = File.ReadAllText(Path.Combine(root, "src", "InventoryAggregation.Api", "InventoryAggregation.Api.csproj"));
        Assert.Contains("<UserSecretsId>inventory-aggregation-7d3f2c1e</UserSecretsId>", project, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "service-configuration/Environment variable")]
    public async Task The_key_is_read_from_the_environment_variable()
    {
        const string variable = "InventoryApi__ApiKey";
        const string value = "env-test-key-91c3";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            var upstream = new FakeUpstream(TimeProvider.System);
            await using var api = new TestApi { Upstream = (request, _, _) => Task.FromResult(upstream.Respond(request)) };
            api.Settings.Remove("InventoryApi:ApiKey");

            using var response = await api.CreateClient().GetAsync("/api/inventory/categories", Ct);

            Assert.True(response.IsSuccessStatusCode);
            Assert.All(api.UpstreamHandler!.Requests, r => Assert.Equal([value], r.Headers["X-Api-Key"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    [Trait("Scenario", "service-configuration/Debug logging on")]
    public async Task With_debug_logging_no_entry_contains_the_key_or_the_upstream_host()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        await using var api = new TestApi
        {
            MinimumLogLevel = LogLevel.Trace,
            Upstream = (request, n, _) => Task.FromResult(n == 2 ? ScriptedHttpHandler.Problem(System.Net.HttpStatusCode.ServiceUnavailable) : upstream.Respond(request)),
        };
        api.Settings["InventoryApi:BaseDelay"] = "00:00:00.010";
        api.Settings["Logging:LogLevel:Default"] = "Trace";
        api.Settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace";

        using var response = await FakeClock.Drive(api.Time, api.CreateClient().GetAsync("/api/inventory/categories", Ct));

        Assert.True(response.IsSuccessStatusCode);
        var logs = api.Logs.GetSnapshot();
        Assert.Contains(logs, r => r.Level == LogLevel.Debug || r.Level == LogLevel.Trace);
        Assert.Contains(logs, r => r.Message.Contains("retrying", StringComparison.Ordinal));
        foreach (var entry in logs)
        {
            var text = $"{entry.Message} {entry.Exception} {string.Join(",", entry.StructuredState?.Select(kv => kv.Value) ?? [])}";
            Assert.DoesNotContain(ClientHarness.ApiKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain("inventory.test", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Scenario", "service-configuration/Swapping the source in tests")]
    public async Task Replacing_the_page_source_leaves_every_other_component_as_registered()
    {
        var fake = new FakePageSource(page => FakePageSource.Healthy(page));
        await using var api = new TestApi { PageSource = fake };

        using var response = await api.CreateClient().GetAsync("/api/inventory/categories", Ct);

        Assert.True(response.IsSuccessStatusCode);
        using var scope = api.Services.CreateScope();
        var services = scope.ServiceProvider;
        Assert.Same(fake, services.GetRequiredService<IInventoryPageSource>());
        Assert.IsType<InventoryCollector>(services.GetRequiredService<InventoryCollector>());
        Assert.IsType<RecordValidator>(services.GetRequiredService<RecordValidator>());
        Assert.IsType<CategoryAggregator>(services.GetRequiredService<CategoryAggregator>());
        Assert.IsType<InventoryReportService>(services.GetRequiredService<InventoryReportService>());
        Assert.Same(api.Time, services.GetRequiredService<TimeProvider>());
        Assert.NotEmpty(fake.Calls);
    }

    /// <summary>Starts a host with the service's registrations and the given settings; returns the startup failure.</summary>
    private static async Task<OptionsValidationException> StartupFailureAsync(Dictionary<string, string?> settings)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InventoryApi:BaseUrl"] = ClientHarness.BaseUrl,
            ["InventoryApi:ApiKey"] = ClientHarness.ApiKey,
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddInventoryAggregation(builder.Configuration);
        using var host = builder.Build();

        return await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Ct));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
