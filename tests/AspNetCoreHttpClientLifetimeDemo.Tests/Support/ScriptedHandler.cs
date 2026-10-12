using System.Net;

namespace AspNetCoreHttpClientLifetimeDemo.Tests.Support;

internal sealed class ScriptedHandler : HttpMessageHandler
{
    public int CallCount { get; private set; }

    public HttpMethod? LastMethod { get; private set; }

    public Uri? LastRequestUri { get; private set; }

    public string? LastAccept { get; private set; }

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Responder { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastMethod = request.Method;
        LastRequestUri = request.RequestUri;
        LastAccept = request.Headers.Accept.ToString();

        if (Responder is not null)
        {
            return Responder(request, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(JsonResponse());
    }

    public static HttpResponseMessage JsonResponse(
        int userId = 1,
        int id = 1,
        string title = "sample title",
        string body = "sample body")
    {
        string json =
            $$"""
            {
              "userId": {{userId}},
              "id": {{id}},
              "title": "{{title}}",
              "body": "{{body}}"
            }
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
