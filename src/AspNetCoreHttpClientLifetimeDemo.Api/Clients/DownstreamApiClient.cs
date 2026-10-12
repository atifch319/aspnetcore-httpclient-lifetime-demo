using System.Net.Http.Json;
using System.Text.Json;
using AspNetCoreHttpClientLifetimeDemo.Models;

namespace AspNetCoreHttpClientLifetimeDemo.Clients;

/// <summary>
/// Typed client registered with <c>AddHttpClient&lt;DownstreamApiClient&gt;()</c>.
/// A new instance can be created per request scope. <c>IHttpClientFactory</c> owns the handlers and their connection pools.
/// </summary>
public sealed class DownstreamApiClient(HttpClient httpClient) : IDisposable
{
    public const string SampleRelativePath = "posts/1";

    /// <summary>
    /// Name <c>AddHttpClient&lt;DownstreamApiClient&gt;()</c> uses for the factory registration.
    /// Startup fails if the framework-assigned name differs.
    /// </summary>
    public const string RegisteredHttpClientName = "DownstreamApiClient";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DownstreamResponse> GetSamplePostAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(
                SampleRelativePath,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            DownstreamResponse? payload = await response.Content
                .ReadFromJsonAsync<DownstreamResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
            {
                throw new HttpRequestException("The downstream service returned an empty response body.");
            }

            return payload;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The incoming request was aborted. This is caller cancellation, not HttpClient.Timeout.
            throw;
        }
        catch (OperationCanceledException exception)
        {
            // HttpClient.Timeout cancels with its own token and surfaces as OperationCanceledException.
            throw new TimeoutException(
                $"The downstream request exceeded the HttpClient timeout of {httpClient.Timeout.TotalSeconds:0} seconds.",
                exception);
        }
    }

    /// <summary>
    /// Disposes the factory-created <see cref="HttpClient"/> wrapper.
    /// The factory keeps the underlying handler and connection pool alive.
    /// </summary>
    public void Dispose() => httpClient.Dispose();
}
