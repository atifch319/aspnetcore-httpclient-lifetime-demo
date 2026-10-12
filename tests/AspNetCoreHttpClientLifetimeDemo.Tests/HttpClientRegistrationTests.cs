using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AspNetCoreHttpClientLifetimeDemo.Clients;
using AspNetCoreHttpClientLifetimeDemo.ExceptionHandling;
using AspNetCoreHttpClientLifetimeDemo.Models;
using AspNetCoreHttpClientLifetimeDemo.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AspNetCoreHttpClientLifetimeDemo.Tests;

public sealed class HttpClientRegistrationTests
{
    [Fact]
    public void Host_starts_without_calling_the_downstream_service()
    {
        var handler = new ScriptedHandler();
        using WebApplicationFactory<Program> factory = DemoApiFactory.Create(handler);

        using HttpClient client = factory.CreateClient();

        Assert.Equal(0, handler.CallCount);
        Assert.NotNull(client.BaseAddress);
    }

    [Fact]
    public async Task Typed_client_registration_invokes_posts_endpoint_with_configured_base_address()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromResult(ScriptedHandler.JsonResponse(
                userId: 4,
                id: 1,
                title: "factory",
                body: "shared handler"))
        };

        await using WebApplicationFactory<Program> factory = DemoApiFactory.Create(
            handler,
            DemoApiFactory.DownstreamSettings(baseUrl: "https://downstream.test", timeoutSeconds: "7"));

        using IServiceScope scope = factory.Services.CreateScope();
        var typedClient = scope.ServiceProvider.GetRequiredService<DownstreamApiClient>();
        Assert.Equal(0, handler.CallCount);

        DownstreamResponse result = await typedClient.GetSamplePostAsync(CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal(new Uri("https://downstream.test/posts/1"), handler.LastRequestUri);
        Assert.Contains("application/json", handler.LastAccept, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, result.UserId);
        Assert.Equal("factory", result.Title);
        Assert.Equal("shared handler", result.Body);
    }

    [Fact]
    public void Base_address_and_timeout_come_from_configuration()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            DemoApiFactory.DownstreamSettings(baseUrl: "https://downstream.test", timeoutSeconds: "7"));

        using IServiceScope scope = factory.Services.CreateScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        using HttpClient httpClient = httpClientFactory.CreateClient(DownstreamApiClient.RegisteredHttpClientName);

        Assert.Equal(new Uri("https://downstream.test/"), httpClient.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(7), httpClient.Timeout);
        Assert.Contains(httpClient.DefaultRequestHeaders.Accept, header => header.MediaType == "application/json");
    }

    [Fact]
    public void Handler_lifetime_stays_at_the_factory_default_when_it_is_not_configured()
    {
        using var factory = new WebApplicationFactory<Program>();
        using IServiceScope scope = factory.Services.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        HttpClientFactoryOptions options = monitor.Get(DownstreamApiClient.RegisteredHttpClientName);

        Assert.Equal(TimeSpan.FromMinutes(2), options.HandlerLifetime);
    }

    [Fact]
    public void Configured_handler_lifetime_is_applied()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            DemoApiFactory.DownstreamSettings(handlerLifetimeMinutes: "5"));

        using IServiceScope scope = factory.Services.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        HttpClientFactoryOptions options = monitor.Get(DownstreamApiClient.RegisteredHttpClientName);

        Assert.Equal(TimeSpan.FromMinutes(5), options.HandlerLifetime);
    }

    [Theory]
    [InlineData("not-a-url", "10")]
    [InlineData("ftp://files.example/data", "10")]
    [InlineData("https://downstream.test/search?q=1", "10")]
    public void Invalid_base_url_prevents_startup(string baseUrl, string timeoutSeconds)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            DemoApiFactory.DownstreamSettings(baseUrl: baseUrl, timeoutSeconds: timeoutSeconds));

        Exception? exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("DownstreamApi:BaseUrl", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_timeout_prevents_startup()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            DemoApiFactory.DownstreamSettings(baseUrl: "https://downstream.test/", timeoutSeconds: "0"));

        Exception? exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("TimeoutSeconds", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_handler_lifetime_prevents_startup()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            DemoApiFactory.DownstreamSettings(
                baseUrl: "https://downstream.test/",
                timeoutSeconds: "10",
                handlerLifetimeMinutes: "0"));

        Exception? exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("HandlerLifetimeMinutes", exception.ToString(), StringComparison.Ordinal);
    }
}

public sealed class DownstreamEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Sample_endpoint_returns_the_downstream_post()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromResult(ScriptedHandler.JsonResponse(
                userId: 21,
                id: 1,
                title: "sample",
                body: "from the fake handler"))
        };

        await using WebApplicationFactory<Program> factory = DemoApiFactory.Create(handler);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/downstream/sample");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        DownstreamResponse? payload = await response.Content.ReadFromJsonAsync<DownstreamResponse>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(21, payload.UserId);
        Assert.Equal(1, payload.Id);
        Assert.Equal("sample", payload.Title);
        Assert.Equal("from the fake handler", payload.Body);
        Assert.Equal(new Uri("https://jsonplaceholder.typicode.com/posts/1"), handler.LastRequestUri);
    }

    [Fact]
    public async Task Sample_endpoint_maps_downstream_http_failures_to_502_without_a_stack_trace()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", Encoding.UTF8, "text/plain")
            })
        };

        await using WebApplicationFactory<Program> factory = DemoApiFactory.Create(handler);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/downstream/sample");
        string body = await response.Content.ReadAsStringAsync();
        ProblemDetails? problem = JsonSerializer.Deserialize<ProblemDetails>(body, JsonOptions);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Bad Gateway", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status502BadGateway, problem.Status);
        Assert.Equal("The downstream service request failed.", problem.Detail);
    }

    [Fact]
    public async Task Sample_endpoint_maps_malformed_downstream_json_to_502()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{", Encoding.UTF8, "application/json")
            })
        };

        await using WebApplicationFactory<Program> factory = DemoApiFactory.Create(handler);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/downstream/sample");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("JsonException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sample_endpoint_maps_an_outbound_timeout_to_504()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("Simulated outbound timeout."))
        };

        await using WebApplicationFactory<Program> factory = DemoApiFactory.Create(handler);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/downstream/sample");
        string body = await response.Content.ReadAsStringAsync();
        ProblemDetails? problem = JsonSerializer.Deserialize<ProblemDetails>(body, JsonOptions);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Gateway Timeout", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeoutException", body, StringComparison.Ordinal);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, problem.Status);
        Assert.Equal("The downstream request timed out.", problem.Detail);
    }

    [Fact]
    public async Task Canceled_request_is_not_mapped_to_gateway_timeout()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler
        {
            Responder = (_, cancellationToken) =>
            {
                started.TrySetResult();
                var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
                return pending.Task;
            }
        };

        await using WebApplicationFactory<Program> factory = DemoApiFactory.Create(handler);
        using HttpClient client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        using var cts = new CancellationTokenSource();
        Task<HttpResponseMessage> pendingRequest = client.GetAsync("/api/downstream/sample", cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        Exception? exception = await Record.ExceptionAsync(() => pendingRequest);

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.False(exception is TimeoutException);
    }

    [Fact]
    public async Task Exception_handler_does_not_convert_caller_cancellation_into_a_response()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new DownstreamExceptionHandler(NullLogger<DownstreamExceptionHandler>.Instance);
        var context = new DefaultHttpContext
        {
            RequestAborted = cts.Token,
            Response =
            {
                Body = new MemoryStream()
            }
        };

        bool handled = await handler.TryHandleAsync(
            context,
            new OperationCanceledException(cts.Token),
            CancellationToken.None);

        Assert.False(handled);
        Assert.NotEqual(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }
}
