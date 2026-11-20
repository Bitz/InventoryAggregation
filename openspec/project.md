# Inventory Aggregation Service — project context

## Purpose

Stock reporting needs one figure set per product category: what is on hand, what it is worth, and
what changed last. The figures come from the upstream **Inventory API**, a read-only service that
returns inventory records one page at a time and does not always behave. This service collects the
whole dataset, proves to itself that what it collected is complete and consistent, aggregates it by
category, and serves the result over its own HTTP endpoint.

Correctness of the collected dataset matters more than breadth of features. A figure computed from a
partial or inconsistent dataset is worse than no figure, so the service refuses to answer rather than
answer wrongly, and says why.

## Tech stack

- C# on **.NET 10 (LTS)**. .NET 8 left support on 2026-11-10, so it is not a target. `global.json`
  pins the SDK band with `rollForward: latestFeature`.
- ASP.NET Core minimal API for the host and endpoint.
- `IHttpClientFactory` typed client for the upstream, with `Microsoft.Extensions.Http.Resilience`
  (Polly v8) for retries and timeouts.
- `System.Text.Json` for (de)serialization; money is `decimal` end to end.
- xUnit for tests, `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) so retry and
  timeout tests do not sleep, `Microsoft.AspNetCore.Mvc.Testing` for endpoint tests.

## Solution layout

| Project | Holds | May reference |
|---|---|---|
| `src/InventoryAggregation.Core` | Record model, validation, collection (paging and integrity checks against an abstract page source), aggregation | nothing HTTP or ASP.NET |
| `src/InventoryAggregation.Api` | Host, composition root, endpoint, Problem Details mapping, the upstream HTTP client | Core |
| `tests/InventoryAggregation.Core.Tests` | Validation, collection and aggregation tests against a scripted page source | Core |
| `tests/InventoryAggregation.Api.Tests` | Upstream client resilience tests over a scripted `HttpMessageHandler`, endpoint tests | Api, Core |

Core has no reference to `System.Net.Http` types in its public surface. That is the line that keeps
the aggregation testable without HTTP, and the compiler enforces it.

## Conventions

- Async all the way: no `.Result`, `.Wait()` or `GetAwaiter().GetResult()`. Every async method takes a
  `CancellationToken` and passes it on.
- Expected failures are values, not exceptions. The upstream client returns a page or a classified
  failure; the collector returns a dataset or a classified failure. Exceptions are for bugs.
- Our own services are registered through one extension method, `AddInventoryAggregation(...)`.
- Time comes from an injected `TimeProvider`, never `DateTime.UtcNow`.
- Options are bound and validated at startup (`ValidateOnStart`). A bad setting stops the app with a
  message that names the setting.
- Secrets never enter source control, logs, URLs or error responses.
- Every judgement call the requirements leave open is recorded in `design.md` and repeated in the
  README's assumptions section.

## Domain terms

| Term | Meaning |
|---|---|
| Record | One inventory row from the upstream: `id`, `name`, `category`, `price`, `quantity`, `lastUpdated`. |
| Available record | A valid record with `quantity > 0`. Only these feed the figures. |
| Page envelope | `records`, `page`, `pageSize`, `totalPages`, `totalRecords` as returned per request. |
| Collection | One complete pass over the upstream that yields a verified dataset or a failure. |
| Rejected record | A record that failed validation. Excluded from figures, listed in the response. |

## External dependencies

- **Inventory API**: `GET {BaseUrl}/api/records?page={1-based}&pageSize={1..25}`. Base URL and API key
  are issued per environment and supplied through configuration. The dataset is static between runs,
  so asking again is a valid way to settle a disagreement between two answers.
- Documented errors are `400` (paging out of range) and `500`. Others are possible and must be handled.
  A `200` is not proof of a correct page.
