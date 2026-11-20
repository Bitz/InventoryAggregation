using InventoryAggregation.Api;
using InventoryAggregation.Api.Endpoints;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInventoryAggregation(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<InternalErrorHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Swagger UI renders the built-in document; it does not generate one of its own.
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Inventory Aggregation Service");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "Inventory Aggregation Service";
    });
}

app.MapCategoryReport();

try
{
    await app.RunAsync();
    return 0;
}
catch (OptionsValidationException exception)
{
    // A bad setting stops the app with a message that names it.
    await Console.Error.WriteLineAsync("The service cannot start because its configuration is invalid:");
    foreach (var failure in exception.Failures)
    {
        await Console.Error.WriteLineAsync($"  - {failure}");
    }

    return 1;
}

/// <summary>The application entry point; public so endpoint tests can host it.</summary>
public partial class Program;
