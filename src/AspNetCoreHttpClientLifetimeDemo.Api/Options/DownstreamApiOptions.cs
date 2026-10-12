using System.ComponentModel.DataAnnotations;

namespace AspNetCoreHttpClientLifetimeDemo.Options;

public sealed class DownstreamApiOptions
{
    public const string SectionName = "DownstreamApi";

    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Optional <c>IHttpClientFactory</c> handler lifetime, in minutes.
    /// When omitted, the factory default of two minutes is left unchanged.
    /// </summary>
    [Range(1, 1440)]
    public int? HandlerLifetimeMinutes { get; set; }

    public static bool IsValidBaseUrl(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }

    /// <summary>
    /// Returns an absolute base address with a trailing slash so relative request paths append correctly.
    /// </summary>
    public static Uri CreateBaseAddress(string baseUrl)
    {
        if (!IsValidBaseUrl(baseUrl))
        {
            throw new InvalidOperationException(
                "DownstreamApi:BaseUrl must be an absolute http or https URL without a query or fragment.");
        }

        string normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        return new Uri(normalized, UriKind.Absolute);
    }
}
