using System.Net.Http.Headers;
using AspNetCoreHttpClientLifetimeDemo.Clients;
using AspNetCoreHttpClientLifetimeDemo.ExceptionHandling;
using AspNetCoreHttpClientLifetimeDemo.Options;
using AspNetCoreHttpClientLifetimeDemo.Services;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DownstreamExceptionHandler>();

builder.Services
    .AddOptions<DownstreamApiOptions>()
    .BindConfiguration(DownstreamApiOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(
        options => DownstreamApiOptions.IsValidBaseUrl(options.BaseUrl),
        "DownstreamApi:BaseUrl must be an absolute http or https URL without a query or fragment.")
    .ValidateOnStart();

IHttpClientBuilder httpClientBuilder = builder.Services.AddHttpClient<DownstreamApiClient>((serviceProvider, client) =>
{
    DownstreamApiOptions options = serviceProvider.GetRequiredService<IOptions<DownstreamApiOptions>>().Value;
    client.BaseAddress = DownstreamApiOptions.CreateBaseAddress(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

if (!string.Equals(httpClientBuilder.Name, DownstreamApiClient.RegisteredHttpClientName, StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        $"The typed client was registered as '{httpClientBuilder.Name}', expected '{DownstreamApiClient.RegisteredHttpClientName}'.");
}

// Applied when the factory builds handler options, so configuration overrides are visible.
// Omitted HandlerLifetimeMinutes leaves the factory default of two minutes in place.
builder.Services.AddSingleton<IConfigureOptions<HttpClientFactoryOptions>>(serviceProvider =>
{
    IOptionsMonitor<DownstreamApiOptions> monitor = serviceProvider.GetRequiredService<IOptionsMonitor<DownstreamApiOptions>>();
    return new ConfigureNamedOptions<HttpClientFactoryOptions>(httpClientBuilder.Name, factoryOptions =>
    {
        if (monitor.CurrentValue.HandlerLifetimeMinutes is int minutes)
        {
            factoryOptions.HandlerLifetime = TimeSpan.FromMinutes(minutes);
        }
    });
});

builder.Services.AddScoped<DownstreamService>();

WebApplication app = builder.Build();

// Always map downstream failures ourselves so responses do not include exception stack traces.
app.UseExceptionHandler();
app.MapControllers();
app.Run();

public partial class Program;
