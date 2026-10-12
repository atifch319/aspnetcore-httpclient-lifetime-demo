using System.Net;
using System.Text;
using AspNetCoreHttpClientLifetimeDemo.Clients;
using AspNetCoreHttpClientLifetimeDemo.Models;
using AspNetCoreHttpClientLifetimeDemo.Services;
using AspNetCoreHttpClientLifetimeDemo.Tests.Support;

namespace AspNetCoreHttpClientLifetimeDemo.Tests;

public sealed class DownstreamServiceTests
{
    [Fact]
    public async Task GetSampleAsync_returns_the_typed_client_result()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromResult(ScriptedHandler.JsonResponse(
                userId: 3,
                id: 8,
                title: "typed client",
                body: "service does not call HttpClient directly"))
        };

        using ClientScope scope = CreateService(handler);

        DownstreamResponse result = await scope.Service.GetSampleAsync(CancellationToken.None);

        Assert.Equal(3, result.UserId);
        Assert.Equal(8, result.Id);
        Assert.Equal("typed client", result.Title);
        Assert.Equal(new Uri("https://downstream.test/posts/1"), handler.LastRequestUri);
    }

    [Fact]
    public async Task GetSampleAsync_propagates_downstream_http_failures()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream failed", Encoding.UTF8, "text/plain")
            })
        };

        using ClientScope scope = CreateService(handler);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => scope.Service.GetSampleAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
    }

    [Fact]
    public async Task GetSampleAsync_propagates_caller_cancellation()
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

        using ClientScope scope = CreateService(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Service.GetSampleAsync(cts.Token));
    }

    [Fact]
    public async Task GetSampleAsync_propagates_timeouts()
    {
        var handler = new ScriptedHandler
        {
            Responder = (_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated timeout"))
        };

        using ClientScope scope = CreateService(handler);

        await Assert.ThrowsAsync<TimeoutException>(() => scope.Service.GetSampleAsync(CancellationToken.None));
    }

    private static ClientScope CreateService(ScriptedHandler handler)
    {
        return new ClientScope(handler);
    }

    private sealed class ClientScope : IDisposable
    {
        private readonly DownstreamApiClient _client;

        public ClientScope(ScriptedHandler handler)
        {
            var httpClient = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://downstream.test/"),
                Timeout = TimeSpan.FromSeconds(10)
            };

            _client = new DownstreamApiClient(httpClient);
            Service = new DownstreamService(_client);
        }

        public DownstreamService Service { get; }

        public void Dispose() => _client.Dispose();
    }
}
