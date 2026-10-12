using System.Net;
using System.Text;
using System.Text.Json;
using AspNetCoreHttpClientLifetimeDemo.Clients;
using AspNetCoreHttpClientLifetimeDemo.Models;
using AspNetCoreHttpClientLifetimeDemo.Tests.Support;

namespace AspNetCoreHttpClientLifetimeDemo.Tests;

public sealed class DownstreamApiClientTests
{
    private static readonly Uri BaseAddress = new("https://downstream.test/");

    [Fact]
    public async Task GetSamplePostAsync_deserializes_a_successful_response()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromResult(ScriptedHandler.JsonResponse(
                userId: 11,
                id: 1,
                title: "connection pooling",
                body: "handlers own the sockets"))
        };

        using DownstreamApiClient client = CreateClient(handler);

        DownstreamResponse result = await client.GetSamplePostAsync(CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal(new Uri("https://downstream.test/posts/1"), handler.LastRequestUri);
        Assert.Equal(11, result.UserId);
        Assert.Equal(1, result.Id);
        Assert.Equal("connection pooling", result.Title);
        Assert.Equal("handlers own the sockets", result.Body);
    }

    [Fact]
    public async Task GetSamplePostAsync_throws_when_downstream_returns_a_non_success_status()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"error":"unavailable"}""", Encoding.UTF8, "application/json")
            })
        };

        using DownstreamApiClient client = CreateClient(handler);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetSamplePostAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetSamplePostAsync_throws_when_the_success_body_is_not_a_post()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json")
            })
        };

        using DownstreamApiClient client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetSamplePostAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetSamplePostAsync_throws_JsonException_when_the_body_is_malformed()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{", Encoding.UTF8, "application/json")
            })
        };

        using DownstreamApiClient client = CreateClient(handler);

        await Assert.ThrowsAsync<JsonException>(() => client.GetSamplePostAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetSamplePostAsync_propagates_caller_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new ScriptedHandler
        {
            Responder = (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(ScriptedHandler.JsonResponse());
            }
        };

        using DownstreamApiClient client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetSamplePostAsync(cts.Token));
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task GetSamplePostAsync_reports_a_timeout_when_the_handler_cancels_without_caller_cancellation()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("Simulated HttpClient timeout."))
        };

        using DownstreamApiClient client = CreateClient(handler);

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(
            () => client.GetSamplePostAsync(CancellationToken.None));

        Assert.IsType<TaskCanceledException>(exception.InnerException);
        Assert.Contains("10", exception.Message, StringComparison.Ordinal);
    }

    private static DownstreamApiClient CreateClient(ScriptedHandler handler)
    {
        var httpClient = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = BaseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };

        return new DownstreamApiClient(httpClient);
    }
}
