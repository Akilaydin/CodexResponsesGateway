using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

static class ModelCatalogProxy
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);

    public static async Task HandleAsync(HttpContext context, GatewayOptions options, HttpClient httpClient)
    {
        var cancellationToken = context.RequestAborted;
        JsonObject nativeCatalog;

        try
        {
            nativeCatalog = await FetchNativeCatalogAsync(context, options, httpClient, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TimeoutException)
        {
            await ProxyHttp.WriteJsonErrorAsync(
                context,
                StatusCodes.Status502BadGateway,
                $"Could not fetch native Codex model catalog: {ex.Message}");
            return;
        }

        if (nativeCatalog["models"] is not JsonArray nativeModels)
        {
            await ProxyHttp.WriteJsonErrorAsync(
                context,
                StatusCodes.Status502BadGateway,
                "Native Codex model catalog has no models array.");
            return;
        }

        var nativeRows = nativeModels.OfType<JsonObject>().ToArray();
        var schemaTemplate = nativeRows
            .FirstOrDefault(m => string.Equals(m["visibility"]?.GetValue<string>(), "list", StringComparison.Ordinal))
            ?? nativeRows.FirstOrDefault();

        if (schemaTemplate is not null)
        {
            var nativeBySlug = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var model in nativeRows)
            {
                if (TryGetSlug(model, out var slug))
                {
                    nativeBySlug.TryAdd(slug, model);
                }
            }

            var knownSlugs = new HashSet<string>(nativeBySlug.Keys, StringComparer.Ordinal);

            foreach (var provider in options.Providers)
            {
                IReadOnlyList<string> upstreamModels;
                try
                {
                    upstreamModels = await FetchProviderModelIdsAsync(provider, httpClient, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TimeoutException)
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

                    nativeBySlug.TryGetValue(upstreamModel, out var matchingNativeModel);
                    nativeModels.Add((JsonNode)ExternalModelCatalogRowFactory.Create(
                        schemaTemplate,
                        matchingNativeModel,
                        provider,
                        upstreamModel));
                }
            }
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(nativeCatalog.ToJsonString(), cancellationToken);
    }

    private static async Task<JsonObject> FetchNativeCatalogAsync(
        HttpContext context,
        GatewayOptions options,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateDiscoveryTimeout(cancellationToken);
        var upstreamUri = ProxyHttp.BuildEndpointUri(options.NativeBaseUrl, "models", context.Request.QueryString.Value);
        using var request = new HttpRequestMessage(HttpMethod.Get, upstreamUri);
        ProxyHttp.CopyRequestHeaders(context.Request, request, externalProvider: false, includeAcceptEncoding: false);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Native catalog returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var root = await JsonNode.ParseAsync(stream, cancellationToken: timeout.Token);
            return root as JsonObject
                ?? throw new JsonException("Native catalog root is not a JSON object.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Native catalog timed out after {DiscoveryTimeout.TotalSeconds:0} seconds.", ex);
        }
    }

    private static async Task<IReadOnlyList<string>> FetchProviderModelIdsAsync(
        ProviderOptions provider,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateDiscoveryTimeout(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, ProxyHttp.BuildEndpointUri(provider.BaseUrl, "models", null));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var root = await JsonNode.ParseAsync(stream, cancellationToken: timeout.Token) as JsonObject
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
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Provider '{provider.Id}' model discovery timed out after {DiscoveryTimeout.TotalSeconds:0} seconds.",
                ex);
        }
    }

    private static CancellationTokenSource CreateDiscoveryTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);
        return timeout;
    }

    private static bool TryGetSlug(JsonObject model, out string slug)
    {
        if (model["slug"] is JsonValue slugValue
            && slugValue.TryGetValue<string>(out var value)
            && !string.IsNullOrWhiteSpace(value))
        {
            slug = value;
            return true;
        }

        slug = string.Empty;
        return false;
    }
}
