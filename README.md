# HTTP Connection Pooling in .NET — Why HttpClient Lifetime Matters

This sample is an ASP.NET Core Web API that calls a downstream HTTP service through a typed `HttpClient` registered with `IHttpClientFactory`. It shows why the lifetime of an `HttpClient` matters, and it keeps handler lifetime, pooled connection lifetime, request timeout, and caller cancellation as separate controls.

The API does not open a downstream connection during startup. Tests use a fake `HttpMessageHandler` or the in-process test server and do not call the public internet.

## Problem statement

Applications that call HTTP APIs need to reuse TCP connections. Opening a new connection for every request costs handshakes, ephemeral ports, and time. .NET already pools connections, but the pool lives on the **handler**, not on the act of constructing an `HttpClient`.

If each request creates an `HttpClient` with its own handler and then disposes that pair, later requests cannot reuse the previous pool. Under sustained load, closed sockets can remain in TCP cleanup states long enough to exhaust local ports. That failure mode is usually described as socket exhaustion.

Creating an `HttpClient` object does **not** by itself open a TCP connection. A connection is opened when a handler sends a request and needs one. The harmful pattern is repeated creation of separate handlers, not the mere existence of a client wrapper.

## Why HttpClient lifetime matters

`HttpClient` is a thin wrapper around an `HttpMessageHandler`. In modern .NET that handler is typically a `SocketsHttpHandler`, and that handler owns the connection pool.

- Dispose the handler, and its pool is no longer available to later requests.
- Share one handler, and many `HttpClient` instances can reuse the same pool.
- Keep one handler forever, and existing connections may continue to use an IP address that DNS no longer returns.

Lifetime decisions are about the handler and the connections it caches. They are not a reason to make `HttpClient.Timeout` infinite, and they are not a reason to ignore the caller's `CancellationToken`.

## How handlers manage connections

`SocketsHttpHandler` keeps idle connections and hands them to later requests that target the same host. A request reuses a pooled connection when the handler still has one that is eligible. Otherwise the handler opens a connection at send time.

Two clients share a pool only when they share a handler (or a handler pool managed by the factory). Two clients constructed with `new HttpClient()` do not share a pool, because each default constructor creates and owns a distinct handler. Disposing the client disposes that handler.

This project does not include a socket-exhaustion stress test. The per-request example exists so the pattern is visible in code, and so it is obvious that the running API does not use it.

## Per-request vs long-lived vs IHttpClientFactory

| Approach | Handler | Connection reuse | DNS | When to use it |
| --- | --- | --- | --- | --- |
| New `HttpClient` per request | Default constructor creates a handler and disposal destroys it | Later requests do not reuse that pool. Sockets can sit in cleanup states. | New connections see current DNS, at the cost of reuse. | Do not use this for application traffic. `HttpClientLifetimeExamples.PerRequestClientAsync` is an illustration only. |
| One long-lived `HttpClient` | One `SocketsHttpHandler` for the process | Strong reuse within that handler | Set `PooledConnectionLifetime` so the pool drops old connections and later connects resolve DNS again | Valid when the process owns a single handler and configures it deliberately. See `HttpClientLifetimeExamples.CreateLongLivedClient`. |
| `IHttpClientFactory` typed client | Factory pools handlers and retires them on a handler lifetime | Requests share the active handler's pool. Clients themselves can be short-lived. | Retiring a handler discards its pool, so a later handler resolves DNS again | The approach this API uses. It is the usual production default, not the only correct design. |

`IHttpClientFactory` is not required for correct pooling. A long-lived client with an explicit `SocketsHttpHandler` is also valid. The factory is the better default for most ASP.NET Core apps because it rotates handlers for you and works cleanly with typed clients, options, and tests.

### Per-request client

```csharp
using var client = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(10)
};

using HttpResponseMessage response = await client.GetAsync(
    requestUri,
    HttpCompletionOption.ResponseHeadersRead,
    cancellationToken);
```

Each call builds a client and a handler, then disposes both. No TCP connection is opened until `GetAsync` sends. After disposal, that handler's connections are not reused by the next call. Do not run this method in a loop to "see" port exhaustion.

### Long-lived client

```csharp
var handler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    ConnectTimeout = TimeSpan.FromSeconds(10)
};

var client = new HttpClient(handler, disposeHandler: true)
{
    BaseAddress = baseAddress,
    Timeout = TimeSpan.FromSeconds(10)
};
```

Keep that client for the life of the process and dispose it on shutdown. Disposing it after every request, with `disposeHandler: true`, brings back the per-request handler problem.

### IHttpClientFactory

`Program.cs` registers the typed client used by the API:

```csharp
builder.Services.AddHttpClient<DownstreamApiClient>((serviceProvider, client) =>
{
    DownstreamApiOptions options = serviceProvider.GetRequiredService<IOptions<DownstreamApiOptions>>().Value;
    client.BaseAddress = DownstreamApiOptions.CreateBaseAddress(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});
```

The factory creates the `HttpClient` instances that are injected into `DownstreamApiClient`. Disposing one of those clients does not dispose the pooled handler. `DownstreamService` is scoped and receives a typed client; outbound HTTP stays inside `DownstreamApiClient`.

When `DownstreamApi:HandlerLifetimeMinutes` is omitted, the factory's two-minute handler lifetime is left as-is. Setting the option changes only that handler lifetime.

## Connection-pooling diagram

```text
API Request
    |
    v
Controller
    |
    v
Downstream Service
    |
    v
Typed HttpClient
    |
    v
IHttpClientFactory-managed handler
    |
    v
Connection pool
    |
    v
Downstream API
```

`GET /api/downstream/sample` follows this path and calls `GET posts/1` on the configured base URL.

## DNS refresh and PooledConnectionLifetime

DNS results can change while a process is running. A connection that is already in a pool keeps talking to the address it was opened against. Recycling **connections** or recycling **handlers** lets a later request resolve the name again.

`SocketsHttpHandler.PooledConnectionLifetime` is the maximum time a pooled connection should be reused. After that, the handler opens a new connection when a request needs one, and that new connection uses current DNS. In-flight work is not canceled when the lifetime elapses. Idle connections are governed separately by `PooledConnectionIdleTimeout`.

`PooledConnectionLifetime` is not `HttpClient.Timeout`. The timeout bounds a single attempt. The pooled connection lifetime bounds how long a connection stays eligible for reuse.

`IHttpClientFactory` refreshes DNS by a different knob: handler lifetime. The default is two minutes. When a handler expires, the factory stops handing it to new clients. Requests already running on it finish, and new requests get a new handler with an empty pool, which resolves DNS again. Handler lifetime is not the same thing as `PooledConnectionLifetime`, and neither one is the request timeout.

## Difference between HttpClient.Timeout and caller cancellation

| Concept | Question it answers | What this project does |
| --- | --- | --- |
| Handler lifetime | How long may the factory keep a handler and its pool? | Factory default of 2 minutes, or `DownstreamApi:HandlerLifetimeMinutes` |
| `PooledConnectionLifetime` | How long may one pooled connection be reused? | Set on the illustrative long-lived handler. Not used as a request deadline. |
| `HttpClient.Timeout` | How long may this outbound attempt run? | `DownstreamApi:TimeoutSeconds` (1–300). A finite timeout is required. |
| Caller `CancellationToken` | Did the incoming request abort? | Propagated into `GetAsync` and into reading the body |

`HttpClient.Timeout` cancels the outbound attempt with the client's own token. That surfaces as `OperationCanceledException` / `TaskCanceledException` even when the caller did not cancel. `DownstreamApiClient` treats that case as a timeout and throws `TimeoutException`, preserving the original exception as `InnerException`.

If the caller's token is already canceled, the same exception type means caller cancellation. The client rethrows it. The API does **not** turn that into `504 Gateway Timeout`. `DownstreamExceptionHandler` also refuses to write a gateway response when `HttpContext.RequestAborted` is canceled.

Other failures stay specific:

- A non-success status code throws `HttpRequestException` via `EnsureSuccessStatusCode()`. The API returns **502 Bad Gateway**.
- A success status with a body that is not the expected JSON throws `JsonException` or `HttpRequestException`. The API returns **502 Bad Gateway**. Nothing in that path invents a successful payload.
- An outbound timeout returns **504 Gateway Timeout**.
- Response bodies are problem details (`application/problem+json`) with a status, title, and stable detail. Stack traces are logged on the server and are not written to the client.

Retries are not configured. `GET posts/1` is idempotent, but a retry policy is optional and is easy to apply badly to non-idempotent methods, so this sample leaves it out.

## Typed-client architecture

- `DownstreamApiOptions` binds the `DownstreamApi` configuration section and is validated on startup. The base URL must be an absolute `http` or `https` URL without a query or fragment. The timeout must be from 1 to 300 seconds. An invalid configuration stops the host; it does not wait for the first request, and it does not contact the downstream host to validate the URL.
- `DownstreamApiClient` takes `HttpClient` in its constructor. It sends `GET posts/1` with `HttpCompletionOption.ResponseHeadersRead`, checks the status code, then reads the JSON body asynchronously. The response is disposed with a `using` declaration.
- `DownstreamService` is the only type the controller calls. It does not build HTTP requests itself.
- `DownstreamController` exposes `GET /api/downstream/sample` and passes `HttpContext.RequestAborted` through as the `CancellationToken`.
- Relative request paths are appended to the base URL. The registration adds a trailing slash when the configured URL does not have one, so a base path such as `https://localhost:5005/api/` keeps the `/api/` prefix.

## Project structure

```text
AspNetCoreHttpClientLifetimeDemo/
├── src/
│   └── AspNetCoreHttpClientLifetimeDemo.Api/
│       ├── Clients/
│       │   └── DownstreamApiClient.cs
│       ├── Controllers/
│       │   └── DownstreamController.cs
│       ├── Examples/
│       │   └── HttpClientLifetimeExamples.cs
│       ├── ExceptionHandling/
│       │   └── DownstreamExceptionHandler.cs
│       ├── Models/
│       │   └── DownstreamResponse.cs
│       ├── Options/
│       │   └── DownstreamApiOptions.cs
│       ├── Properties/
│       │   └── launchSettings.json
│       ├── Services/
│       │   └── DownstreamService.cs
│       ├── Program.cs
│       ├── appsettings.json
│       └── AspNetCoreHttpClientLifetimeDemo.Api.csproj
├── tests/
│   └── AspNetCoreHttpClientLifetimeDemo.Tests/
│       ├── Support/
│       ├── DownstreamApiClientTests.cs
│       ├── DownstreamServiceTests.cs
│       ├── HttpClientRegistrationTests.cs
│       └── AspNetCoreHttpClientLifetimeDemo.Tests.csproj
├── Directory.Build.props
├── AspNetCoreHttpClientLifetimeDemo.sln
├── .gitignore
├── LICENSE
└── README.md
```

`HttpClientLifetimeExamples` is compiled so the snippets stay valid C#, but it is not registered in dependency injection and the API never calls it.

## Prerequisites

- .NET 10 SDK (`net10.0`)
- C# 14, as set by `Directory.Build.props`

No database, frontend, or external test service is required. The sample downstream URL in configuration is [JSONPlaceholder](https://jsonplaceholder.typicode.com/). The process can start when that host is unreachable.

## Configuration

`src/AspNetCoreHttpClientLifetimeDemo.Api/appsettings.json`:

```json
{
  "DownstreamApi": {
    "BaseUrl": "https://jsonplaceholder.typicode.com/",
    "TimeoutSeconds": 10
  }
}
```

Optional handler lifetime, in minutes:

```json
{
  "DownstreamApi": {
    "BaseUrl": "https://jsonplaceholder.typicode.com/",
    "TimeoutSeconds": 10,
    "HandlerLifetimeMinutes": 2
  }
}
```

Omit `HandlerLifetimeMinutes` to keep the factory default of two minutes. There are no secrets in this configuration.

### Local test server

Point `BaseUrl` at any server that serves `GET {base}/posts/1` and returns JSON with `userId`, `id`, `title`, and `body`. Include a trailing slash when the base URL has a path prefix.

```json
{
  "DownstreamApi": {
    "BaseUrl": "http://localhost:5005/",
    "TimeoutSeconds": 10
  }
}
```

Environment variables override the file without editing it. ASP.NET Core uses a double underscore for nested keys:

```powershell
$env:DownstreamApi__BaseUrl = "http://localhost:5005/"
$env:DownstreamApi__TimeoutSeconds = "10"
```

Restart the API after changing either the file or the variables. Startup still does not call the local server; the first `GET /api/downstream/sample` does.

## Run instructions

From the repository root:

```powershell
dotnet restore
dotnet build -c Release
dotnet test
dotnet run --project .\src\AspNetCoreHttpClientLifetimeDemo.Api\
```

The launch profile binds `http://localhost:5080` and does not open a browser.

## Example API request

```powershell
Invoke-RestMethod http://localhost:5080/api/downstream/sample
```

A successful call returns the downstream post, for example a JSON object with `userId`, `id`, `title`, and `body`.

Downstream HTTP failures are returned as **502** problem details. Outbound timeouts are returned as **504** problem details. Canceling the inbound request does not produce either of those status codes.

## Test instructions

```powershell
dotnet test
```

The suite covers:

- Successful deserialization of a downstream post, including the `GET posts/1` request URI
- Non-success status codes (`HttpRequestException`, then **502** at the HTTP boundary)
- Malformed success bodies (no fabricated success payload; **502** at the HTTP boundary)
- Caller cancellation, including an in-flight request that must not be reported as a gateway timeout
- A handler cancellation that is not the caller's token, mapped to `TimeoutException` and **504**
- Typed-client registration, base address, timeout, `Accept: application/json`, and invocation
- Default and configured handler lifetimes
- Startup validation for the URL, timeout, and handler lifetime
- Host startup with zero downstream calls

Tests are deterministic. Timeout behavior is simulated by a fake handler that cancels without the caller token, so the suite does not wait on `HttpClient.Timeout` and does not open a real socket to a public API.

## Production considerations

- Prefer `IHttpClientFactory` with a typed or named client for ASP.NET Core applications. A single long-lived `HttpClient` backed by a configured `SocketsHttpHandler` remains a valid alternative.
- Do not construct a new `HttpClient()` per request in production code.
- Keep a finite `HttpClient.Timeout`. Do not use it as a substitute for `PooledConnectionLifetime` or handler lifetime.
- Set `PooledConnectionLifetime` when you own a long-lived `SocketsHttpHandler` and DNS for that host can change.
- Leave handler lifetime near the two-minute default unless you have a measured reason to rotate faster or slower. Very short lifetimes reduce connection reuse.
- Propagate the request `CancellationToken`. Do not map caller abort to **504**.
- Return **502** for downstream protocol and payload failures. Log the exception server-side; do not return the stack trace to clients.
- Add retries only as an explicit policy, and only for operations that are safe to repeat. This sample does not retry.
- Do not load-test the per-request anti-pattern against a real network to prove socket exhaustion.

## License

MIT License. Copyright (c) 2026 Muhammad Atif. See [LICENSE](LICENSE).
