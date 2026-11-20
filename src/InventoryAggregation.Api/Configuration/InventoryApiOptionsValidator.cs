using System.Text.RegularExpressions;
using InventoryAggregation.Core;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Configuration;

/// <summary>
/// Enforces the rules in design.md §8. Every message names the setting as it is written in
/// configuration (<c>InventoryApi:Name</c>) and never repeats a secret value.
/// </summary>
internal sealed partial class InventoryApiOptionsValidator : IValidateOptions<InventoryApiOptions>
{
    private const string Section = InventoryApiOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, InventoryApiOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            failures.Add(
                $"{Section}:BaseUrl is not set. Locally: dotnet user-secrets set \"{Section}:BaseUrl\" \"<base URL>\" --id inventory-aggregation-7d3f2c1e. " +
                $"Elsewhere: set the environment variable {Section}__BaseUrl.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl) || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{Section}:BaseUrl must be an absolute http or https URI.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add(
                $"{Section}:ApiKey is not set. Locally: dotnet user-secrets set \"{Section}:ApiKey\" \"<API key>\" --id inventory-aggregation-7d3f2c1e. " +
                $"Elsewhere: set the environment variable {Section}__ApiKey.");
        }

        if (string.IsNullOrEmpty(options.ApiKeyHeader) || !HeaderToken().IsMatch(options.ApiKeyHeader))
        {
            failures.Add($"{Section}:ApiKeyHeader must be a valid HTTP header name.");
        }

        Range(failures, nameof(options.PageSize), options.PageSize, 1, 25);
        Range(failures, nameof(options.MaxConcurrency), options.MaxConcurrency, 1, 16);
        Range(failures, nameof(options.MaxRetries), options.MaxRetries, 0, 10);
        Positive(failures, nameof(options.BaseDelay), options.BaseDelay);

        if (options.MaxRetryAfter < TimeSpan.Zero)
        {
            failures.Add($"{Section}:MaxRetryAfter is {options.MaxRetryAfter}; it must be zero or more.");
        }

        Positive(failures, nameof(options.AttemptTimeout), options.AttemptTimeout);

        if (options.PageTimeout < options.AttemptTimeout)
        {
            failures.Add($"{Section}:PageTimeout is {options.PageTimeout}; it must be at least {Section}:AttemptTimeout ({options.AttemptTimeout}).");
        }

        if (options.CollectionTimeout < options.PageTimeout)
        {
            failures.Add($"{Section}:CollectionTimeout is {options.CollectionTimeout}; it must be at least {Section}:PageTimeout ({options.PageTimeout}).");
        }

        Range(failures, nameof(options.PageRefetchLimit), options.PageRefetchLimit, 0, 5);
        Range(failures, nameof(options.CollectionAttempts), options.CollectionAttempts, 1, 3);

        if (options.MaxPages < 1)
        {
            failures.Add($"{Section}:MaxPages is {options.MaxPages}; it must be 1 or more.");
        }

        if (!Enum.IsDefined(options.InvalidRecordPolicy))
        {
            failures.Add($"{Section}:InvalidRecordPolicy must be {nameof(InvalidRecordPolicy.Report)} or {nameof(InvalidRecordPolicy.Fail)}.");
        }

        Range(failures, nameof(options.RateLimitPermits), options.RateLimitPermits, 1, 100);
        Positive(failures, nameof(options.RateLimitWindow), options.RateLimitWindow);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Range(List<string> failures, string setting, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            failures.Add($"{Section}:{setting} is {value}; it must be from {min} to {max}.");
        }
    }

    private static void Positive(List<string> failures, string setting, TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{Section}:{setting} is {value}; it must be greater than zero.");
        }
    }

    // RFC 9110 token characters.
    [GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderToken();
}
