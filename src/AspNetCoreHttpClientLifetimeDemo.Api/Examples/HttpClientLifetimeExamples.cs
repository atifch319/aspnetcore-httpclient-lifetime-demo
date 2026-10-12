namespace AspNetCoreHttpClientLifetimeDemo.Examples;

/// <summary>
/// Illustrations of HttpClient lifetime. Nothing in this type is registered in DI or called by the API.
/// Do not execute <see cref="PerRequestClientAsync"/> in a loop or as a socket-exhaustion test.
/// </summary>
public static class HttpClientLifetimeExamples
{
    /// <summary>
    /// Anti-pattern. Each call constructs an <see cref="HttpClient"/> with its own handler and then disposes both.
    /// A new client object does not by itself open a TCP connection, but a distinct handler cannot reuse another
    /// handler's pool. Repeated creation can leave sockets in connection cleanup states.
    /// </summary>
    public static async Task<string> PerRequestClientAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        using HttpResponseMessage response = await client.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Valid long-lived client. Keep the returned instance for the process lifetime and dispose it on shutdown.
    /// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> limits how long a pooled connection is reused so
    /// later requests can observe DNS changes. It is not a request timeout.
    /// </summary>
    public static HttpClient CreateLongLivedClient(Uri baseAddress, TimeSpan pooledConnectionLifetime)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = pooledConnectionLifetime,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };
    }
}
