## ADDED Requirements

### Requirement: A page is requested through a factory-managed typed client
The service SHALL fetch a page with `GET {BaseUrl}/api/records?page={page}&pageSize={pageSize}` using
an `HttpClient` supplied by `IHttpClientFactory` to a typed client (`InventoryApiClient`). The client
SHALL NOT be constructed per call or held in a self-managed static. Every request SHALL carry the API
key in the header named by `ApiKeyHeader`, and SHALL NOT carry it in the URL.

#### Scenario: Request shape
- **WHEN** the collector asks for page 3 with page size 25
- **THEN** one request is sent to `{BaseUrl}/api/records?page=3&pageSize=25` with the API key header set
- **AND** the URL contains no credential

#### Scenario: Client lifetime
- **WHEN** two reports are requested one after the other
- **THEN** both obtain their `HttpClient` from the factory and neither creates an `HttpClientHandler`

### Requirement: The client returns a classified result instead of throwing
`IInventoryPageSource.GetPageAsync` SHALL return either a parsed page or a failure carrying a kind from
`design.md` §5, the page number, the last status code if any, and the attempt count. Non-success
statuses, transport errors and timeouts SHALL NOT escape as exceptions. Cancellation requested by the
caller SHALL surface as `OperationCanceledException`, not as a failure.

#### Scenario: Upstream unreachable
- **WHEN** every attempt for a page ends in a connection error
- **THEN** the result is a failure of kind `upstream-unavailable` naming the page and attempt count
- **AND** no exception reaches the caller

#### Scenario: Caller cancels
- **WHEN** the caller's token is cancelled while a request is in flight
- **THEN** the request is aborted and `OperationCanceledException` is thrown
- **AND** no further retry is attempted

### Requirement: Transient failures are retried with bounded exponential backoff
The client SHALL treat as transient: `HttpRequestException`, a per-attempt timeout, and the statuses
`408`, `429`, `500`, `502`, `503` and `504`. It SHALL retry a transient failure up to `MaxRetries`
times (default 3, so 4 attempts) with exponential backoff from `BaseDelay` (default 200 ms) and jitter.

#### Scenario: One server error, then success
- **WHEN** page 2 returns `500` and then `200`
- **THEN** the page result is a success after 2 attempts

#### Scenario: Retries exhausted
- **WHEN** page 2 returns `503` without `Retry-After` on all 4 attempts
- **THEN** the result is a failure of kind `upstream-unavailable` with 4 attempts and last status `503`

### Requirement: Requests are paced to the upstream's rate limit
All requests to the upstream SHALL pass through one rate limiter shared by the whole application,
allowing at most `RateLimitPermits` requests (default 5) per `RateLimitWindow` (default 10 s), every
retry included. A request over the limit SHALL wait for a permit rather than be sent, and SHALL stop
waiting when its caller cancels or the collection deadline passes (`design.md` D22).

#### Scenario: A collection larger than one window
- **WHEN** a collection needs 8 requests under a limit of 5 per 10 s
- **THEN** 5 are sent in the first window, the rest wait for the next, and the service causes no `429` itself

#### Scenario: Two reports at once
- **WHEN** two report requests run together
- **THEN** their upstream requests share the same permits and together never exceed 5 per 10 s

#### Scenario: Throttled anyway
- **WHEN** the upstream still answers `429` with `Retry-After: 6` because another client shares the key
- **THEN** the retry waits 6 s, as for any `Retry-After`

### Requirement: Retry-After is honoured up to a cap
When a transient response carries `Retry-After` (seconds or HTTP-date), the next attempt SHALL wait that
long instead of the backoff delay, if it is no longer than `MaxRetryAfter` (default 10 s). If it is
longer, the client SHALL stop retrying and return `upstream-throttled` with the requested delay.

#### Scenario: Retry-After honoured
- **WHEN** page 2 returns `429` with `Retry-After: 2` and then `200`
- **THEN** the second attempt starts no earlier than 2 s after the first response (asserted with a fake time provider)

#### Scenario: Retry-After too long
- **WHEN** page 2 returns `429` with `Retry-After: 120`
- **THEN** the client does not wait and returns `upstream-throttled` carrying 120 s

### Requirement: Non-transient failures fail fast
The client SHALL NOT retry any status outside the transient set. It SHALL classify `400`, `401`, `403`
and `404` as `upstream-misconfigured`, any other `4xx` as `upstream-rejected`, and any other non-`2xx`
status (including `3xx` left unfollowed and `501`) as `upstream-unexpected-status`. A `2xx` other than
`200` SHALL be treated as `upstream-unexpected-status`.

#### Scenario: Bad credentials
- **WHEN** the upstream returns `401`
- **THEN** exactly one request was sent and the result is `upstream-misconfigured`

#### Scenario: A status nobody documented
- **WHEN** the upstream returns `418`
- **THEN** exactly one request was sent and the result is `upstream-rejected` with status `418`

### Requirement: Every attempt and every page fetch is time-bounded
Each attempt SHALL be cancelled after `AttemptTimeout` (default 10 s) and counted as transient. A page
fetch including all its retries SHALL be cancelled after `PageTimeout` (default 30 s) and returned as
`upstream-unavailable`.

#### Scenario: Upstream hangs
- **WHEN** the upstream accepts the connection and never answers
- **THEN** each attempt ends at `AttemptTimeout`, retries follow, and the page fails no later than `PageTimeout`

### Requirement: A 200 body is parsed into the envelope without trusting it
The client SHALL deserialize a `200` body into the page envelope. A body that is not JSON, or lacks the
`records` array or any of `page`, `pageSize`, `totalPages`, `totalRecords`, SHALL be returned as
`malformed-response` without an HTTP-layer retry (`design.md` D6). Unknown fields SHALL be ignored.
Record fields SHALL be read into nullable raw values so that a bad field in one record does not fail
the page; judging records belongs to `inventory-records`.

#### Scenario: Truncated body
- **WHEN** a `200` body ends mid-object
- **THEN** the result is `malformed-response` for that page

#### Scenario: One record missing its price
- **WHEN** a `200` page has 25 records and one has no `price`
- **THEN** the page parses with 25 raw records, the one with a null price included

### Requirement: Retries are visible and the key is not
The client SHALL log each retry at `Warning` with page, attempt number, status or exception type, and
the delay chosen. The API key header value SHALL be redacted from all logs, including
`IHttpClientFactory` request logging.

#### Scenario: Log content
- **WHEN** a page is retried twice and succeeds
- **THEN** two `Warning` entries name the page, attempts 1 and 2, and their delays
- **AND** no log entry contains the API key
