using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

const string LocalConfigFileName = "gateway.local.json";

var options = LoadOptions();
ValidateOptions(options);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(options.ListenUrl);

var app = builder.Build();

var httpClient = new HttpClient(new SocketsHttpHandler
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

app.MapGet("/health", static () => Results.Text("ok", "text/plain"));

app.MapGet("/v1/models", async (HttpContext context) =>
{
    await HandleModelsAsync(context, options, httpClient);
});

app.MapPost("/v1/responses", async (HttpContext context) =>
{
    await HandleResponsesAsync(context, options, httpClient);
});

app.Run();

static GatewayOptions LoadOptions()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, LocalConfigFileName),
        Path.Combine(Directory.GetCurrentDirectory(), LocalConfigFileName),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", LocalConfigFileName))
    };

    var path = candidates.FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException(
            $"Could not find {LocalConfigFileName}. Checked: {string.Join(", ", candidates.Distinct(StringComparer.OrdinalIgnoreCase))}");

    using var stream = File.OpenRead(path);
    var options = JsonSerializer.Deserialize(stream, GatewayJsonContext.Default.GatewayOptions)
        ?? throw new InvalidOperationException($"{LocalConfigFileName} is empty or invalid.");

    options.ListenUrl ??= "http://127.0.0.1:8320";
    options.NativeBaseUrl ??= "https://chatgpt.com/backend-api/codex";
    options.Providers ??= [];
    return options;
}

static void ValidateOptions(GatewayOptions options)
{
    if (!Uri.TryCreate(options.ListenUrl, UriKind.Absolute, out var listenUri)
        || (listenUri.Scheme != Uri.UriSchemeHttp && listenUri.Scheme != Uri.UriSchemeHttps))
    {
        throw new InvalidOperationException("listenUrl must be an absolute HTTP(S) URL.");
    }

    if (!Uri.TryCreate(options.NativeBaseUrl, UriKind.Absolute, out var nativeUri)
        || (nativeUri.Scheme != Uri.UriSchemeHttp && nativeUri.Scheme != Uri.UriSchemeHttps))
    {
        throw new InvalidOperationException("nativeBaseUrl must be an absolute HTTP(S) URL.");
    }

    var ids = new HashSet<string>(StringComparer.Ordinal);
    var prefixes = new HashSet<string>(StringComparer.Ordinal);

    foreach (var provider in options.Providers)
    {
        if (string.IsNullOrWhiteSpace(provider.Id))
        {
            throw new InvalidOperationException("Provider id must not be empty.");
        }

        if (!ids.Add(provider.Id))
        {
            throw new InvalidOperationException($"Duplicate provider id: {provider.Id}");
        }

        if (string.IsNullOrWhiteSpace(provider.Prefix))
        {
            throw new InvalidOperationException($"Provider {provider.Id} prefix must not be empty.");
        }

        if (!prefixes.Add(provider.Prefix))
        {
            throw new InvalidOperationException($"Duplicate provider prefix: {provider.Prefix}");
        }

        if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var providerUri)
            || (providerUri.Scheme != Uri.UriSchemeHttp && providerUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Provider {provider.Id} baseUrl must be an absolute HTTP(S) URL.");
        }

        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new InvalidOperationException($"Provider {provider.Id} apiKey must not be empty.");
        }
    }
}

static async Task HandleResponsesAsync(HttpContext context, GatewayOptions options, HttpClient httpClient)
{
    var cancellationToken = context.RequestAborted;
    byte[] requestBody;
    JsonNode? body;

    try
    {
        await using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken);
        requestBody = buffer.ToArray();
        body = JsonNode.Parse(requestBody);
    }
    catch (JsonException)
    {
        await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Request body is not valid JSON.");
        return;
    }

    if (body is not JsonObject root
        || root["model"] is not JsonValue modelValue
        || !modelValue.TryGetValue<string>(out var requestedModel)
        || string.IsNullOrWhiteSpace(requestedModel))
    {
        await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Request body must contain a string model field.");
        return;
    }

    var provider = options.Providers.FirstOrDefault(
        p => requestedModel.StartsWith(p.Prefix, StringComparison.Ordinal));

    if (provider is null)
    {
        await ForwardNativeResponseAsync(context, options, httpClient, requestBody, cancellationToken);
        return;
    }

    var upstreamModel = requestedModel[provider.Prefix.Length..];
    if (string.IsNullOrWhiteSpace(upstreamModel))
    {
        await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Model prefix was provided without an upstream model id.");
        return;
    }

    root["model"] = upstreamModel;
    var upstreamUri = BuildEndpointUri(provider.BaseUrl, "responses", context.Request.QueryString.Value);

    using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUri)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(root.ToJsonString()))
    };

    CopyRequestHeaders(context.Request, request, externalProvider: true, includeAcceptEncoding: true);
    request.Content.Headers.ContentType ??= new MediaTypeHeaderValue("application/json");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

    using var response = await httpClient.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);

    await CopyUpstreamResponseAsync(context, response, cancellationToken);
}

static async Task ForwardNativeResponseAsync(
    HttpContext context,
    GatewayOptions options,
    HttpClient httpClient,
    byte[] requestBody,
    CancellationToken cancellationToken)
{
    var upstreamUri = BuildEndpointUri(options.NativeBaseUrl, "responses", context.Request.QueryString.Value);

    using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUri)
    {
        Content = new ByteArrayContent(requestBody)
    };

    CopyRequestHeaders(context.Request, request, externalProvider: false, includeAcceptEncoding: true);
    request.Content.Headers.ContentType ??= new MediaTypeHeaderValue("application/json");

    using var response = await httpClient.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);

    await CopyUpstreamResponseAsync(context, response, cancellationToken);
}

static async Task HandleModelsAsync(HttpContext context, GatewayOptions options, HttpClient httpClient)
{
    var cancellationToken = context.RequestAborted;
    JsonObject nativeCatalog;

    try
    {
        nativeCatalog = await FetchNativeCatalogAsync(context, options, httpClient, cancellationToken);
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
    {
        await WriteJsonErrorAsync(context, StatusCodes.Status502BadGateway, $"Could not fetch native Codex model catalog: {ex.Message}");
        return;
    }

    if (nativeCatalog["models"] is not JsonArray nativeModels)
    {
        await WriteJsonErrorAsync(context, StatusCodes.Status502BadGateway, "Native Codex model catalog has no models array.");
        return;
    }

    var template = nativeModels
        .OfType<JsonObject>()
        .FirstOrDefault(m => string.Equals(m["visibility"]?.GetValue<string>(), "list", StringComparison.Ordinal))
        ?? nativeModels.OfType<JsonObject>().FirstOrDefault();

    if (template is not null)
    {
        var knownSlugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in nativeModels.OfType<JsonObject>())
        {
            if (model["slug"] is JsonValue slugValue && slugValue.TryGetValue<string>(out var slug))
            {
                knownSlugs.Add(slug);
            }
        }

        foreach (var provider in options.Providers)
        {
            IReadOnlyList<string> upstreamModels;
            try
            {
                upstreamModels = await FetchProviderModelIdsAsync(provider, httpClient, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Provider '{provider.Id}' model discovery failed: {ex.Message}");
                continue;
            }

            var excluded = new HashSet<string>(provider.ExcludedModels, StringComparer.Ordinal);
            foreach (var upstreamModel in upstreamModels)
            {
                if (excluded.Contains(upstreamModel))
                {
                    continue;
                }

                var slug = provider.Prefix + upstreamModel;
                if (!knownSlugs.Add(slug))
                {
                    continue;
                }

                var external = template.DeepClone().AsObject();
                external["slug"] = slug;
                external["display_name"] = $"{provider.Id} — {upstreamModel}";
                external["description"] = $"{upstreamModel} via {provider.Id}";
                external["visibility"] = "list";
                external["supported_in_api"] = true;
                external["prefer_websockets"] = false;
                nativeModels.Add((JsonNode)external);
            }
        }
    }

    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(nativeCatalog.ToJsonString(), cancellationToken);
}

static async Task<JsonObject> FetchNativeCatalogAsync(
    HttpContext context,
    GatewayOptions options,
    HttpClient httpClient,
    CancellationToken cancellationToken)
{
    var upstreamUri = BuildEndpointUri(options.NativeBaseUrl, "models", context.Request.QueryString.Value);
    using var request = new HttpRequestMessage(HttpMethod.Get, upstreamUri);
    CopyRequestHeaders(context.Request, request, externalProvider: false, includeAcceptEncoding: false);

    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException($"Native catalog returned HTTP {(int)response.StatusCode}.");
    }

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
    return root as JsonObject
        ?? throw new JsonException("Native catalog root is not a JSON object.");
}

static async Task<IReadOnlyList<string>> FetchProviderModelIdsAsync(
    ProviderOptions provider,
    HttpClient httpClient,
    CancellationToken cancellationToken)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, BuildEndpointUri(provider.BaseUrl, "models", null));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException($"HTTP {(int)response.StatusCode}.");
    }

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject
        ?? throw new JsonException("Provider models response root is not a JSON object.");

    if (root["data"] is not JsonArray data)
    {
        throw new JsonException("Provider models response has no data array.");
    }

    return data
        .OfType<JsonObject>()
        .Select(item => item["id"]?.GetValue<string>())
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Select(id => id!)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

static Uri BuildEndpointUri(string baseUrl, string endpoint, string? queryString)
{
    var url = $"{baseUrl.TrimEnd('/')}/{endpoint}";
    if (!string.IsNullOrEmpty(queryString))
    {
        url += queryString.StartsWith('?') ? queryString : "?" + queryString;
    }

    return new Uri(url, UriKind.Absolute);
}

static void CopyRequestHeaders(
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

static bool ShouldSkipRequestHeader(string name, bool externalProvider, bool includeAcceptEncoding)
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
        || name.Equals("ChatGPT-Account-ID", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Api-Key", StringComparison.OrdinalIgnoreCase);
}

static async Task CopyUpstreamResponseAsync(
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

static void CopyResponseHeaders(HttpHeaders source, IHeaderDictionary destination)
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

static async Task WriteJsonErrorAsync(HttpContext context, int statusCode, string message)
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

sealed class GatewayOptions
{
    [JsonPropertyName("listenUrl")]
    public string ListenUrl { get; set; } = "http://127.0.0.1:8320";

    [JsonPropertyName("nativeBaseUrl")]
    public string NativeBaseUrl { get; set; } = "https://chatgpt.com/backend-api/codex";

    [JsonPropertyName("providers")]
    public List<ProviderOptions> Providers { get; set; } = [];
}

sealed class ProviderOptions
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("prefix")]
    public required string Prefix { get; init; }

    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; init; }

    [JsonPropertyName("apiKey")]
    public required string ApiKey { get; init; }

    [JsonPropertyName("excludedModels")]
    public List<string> ExcludedModels { get; init; } = [];
}

[JsonSerializable(typeof(GatewayOptions))]
internal partial class GatewayJsonContext : JsonSerializerContext;
