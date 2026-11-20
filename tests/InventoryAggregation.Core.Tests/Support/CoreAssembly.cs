using InventoryAggregation.Core.Records;

namespace InventoryAggregation.Core.Tests.Support;

/// <summary>What the Core assembly references. Core decides the numbers and must not know about transport.</summary>
internal static class CoreAssembly
{
    private static readonly string[] Forbidden =
    [
        "System.Net.Http",
        "System.Net.Primitives",
        "System.Text.Json",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.Http",
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.Options",
    ];

    public static IReadOnlyList<string> TransportReferences() =>
        [.. typeof(RecordValidator).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => Forbidden.Any(f => name.StartsWith(f, StringComparison.Ordinal)))];
}
