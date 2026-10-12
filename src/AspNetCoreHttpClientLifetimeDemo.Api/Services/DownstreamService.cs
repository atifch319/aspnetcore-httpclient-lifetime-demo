using AspNetCoreHttpClientLifetimeDemo.Clients;
using AspNetCoreHttpClientLifetimeDemo.Models;

namespace AspNetCoreHttpClientLifetimeDemo.Services;

public sealed class DownstreamService(DownstreamApiClient client)
{
    public Task<DownstreamResponse> GetSampleAsync(CancellationToken cancellationToken)
    {
        return client.GetSamplePostAsync(cancellationToken);
    }
}
