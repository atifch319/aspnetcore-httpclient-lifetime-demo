namespace AspNetCoreHttpClientLifetimeDemo.Models;

public sealed class DownstreamResponse
{
    public int UserId { get; init; }

    public int Id { get; init; }

    public string? Title { get; init; }

    public string? Body { get; init; }
}
