using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

static class ProxyHttp
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
    };

    public static Uri BuildEndpointUri(string baseUrl, string endpoint, string? queryString)
    {
        var url = $"{baseUrl.TrimEnd('/')}/{endpoint}";
        if (!string.IsNullOrEmpty(queryString))
        {
            url += queryString.StartsWith('?') ? queryString : "?" + queryString;
        }

        return new Uri(url, UriKind.Absolute);
    }

    public static void CopyRequestHeaders(
        HttpRequest source,
        HttpRequestMessage destination,
        bool externalProvider,
        bool includeAcceptEncoding)
    {
        foreach (var header in source.Headers)
        {
            if (ShouldSkipRequestHeader(header.Key, externalProvider, includeAcceptEncoding))
            {
                continue;
            }

            var values = header.Value.ToArray();
            if (!destination.Headers.TryAddWithoutValidation(header.Key, values))
            {
                destination.Content?.Headers.TryAddWithoutValidation(header.Key, values);
            }
        }
    }

    public static async Task CopyUpstreamResponseAsync(
        HttpContext context,
        HttpResponseMessage upstream,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)upstream.StatusCode;

        CopyResponseHeaders(upstream.Headers, context.Response.Headers);
        CopyResponseHeaders(upstream.Content.Headers, context.Response.Headers);
        context.Response.Headers.Remove("transfer-encoding");

        await using var stream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(context.Response.Body, cancellationToken);
    }

    public static async Task WriteJsonErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message
            }
        };
        await context.Response.WriteAsync(body.ToJsonString(), context.RequestAborted);
    }

    private static bool ShouldSkipRequestHeader(string name, bool externalProvider, bool includeAcceptEncoding)
    {
        if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
            || (!includeAcceptEncoding && name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!externalProvider)
        {
            return false;
        }

        return name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ChatGPT-Account-ID", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Api-Key", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyResponseHeaders(HttpHeaders source, IHeaderDictionary destination)
    {
        foreach (var header in source)
        {
            if (header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            destination[header.Key] = header.Value.ToArray();
        }
    }
}
