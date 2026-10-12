using AspNetCoreHttpClientLifetimeDemo.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AspNetCoreHttpClientLifetimeDemo.Tests.Support;

internal static class DemoApiFactory
{
    public static WebApplicationFactory<Program> Create(
        ScriptedHandler handler,
        Action<IWebHostBuilder>? configure = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            configure?.Invoke(builder);
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<DownstreamApiClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });
    }

    public static Action<IWebHostBuilder> DownstreamSettings(
        string? baseUrl = null,
        string? timeoutSeconds = null,
        string? handlerLifetimeMinutes = null)
    {
        return builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>();
            if (baseUrl is not null)
            {
                values["DownstreamApi:BaseUrl"] = baseUrl;
            }

            if (timeoutSeconds is not null)
            {
                values["DownstreamApi:TimeoutSeconds"] = timeoutSeconds;
            }

            if (handlerLifetimeMinutes is not null)
            {
                values["DownstreamApi:HandlerLifetimeMinutes"] = handlerLifetimeMinutes;
            }

            config.AddInMemoryCollection(values);
        });
    }
}
