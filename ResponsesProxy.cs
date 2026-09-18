using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZstdSharp;

static class ResponsesProxy
{
    public static async Task HandleAsync(HttpContext context, GatewayOptions options, HttpClient httpClient)
    {
        var cancellationToken = context.RequestAborted;
        byte[] requestBody;
        byte[] jsonBody;
        JsonNode? body;

        await using (var buffer = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(buffer, cancellationToken);
            requestBody = buffer.ToArray();
        }

        try
        {
            jsonBody = DecodeRequestBody(context.Request, requestBody);
            body = JsonNode.Parse(jsonBody);
        }
        catch (NotSupportedException ex)
        {
            await ProxyHttp.WriteJsonErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, ex.Message);
            return;
        }
        catch (InvalidDataException ex)
        {
            await ProxyHttp.WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, ex.Message);
            return;
        }
        catch (JsonException)
        {
            await ProxyHttp.WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Request body is not valid JSON.");
            return;
        }

        if (body is not JsonObject root
            || root["model"] is not JsonValue modelValue
            || !modelValue.TryGetValue<string>(out var requestedModel)
            || string.IsNullOrWhiteSpace(requestedModel))
        {
            await ProxyHttp.WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Request body must contain a string model field.");
            return;
        }

        var provider = options.Providers.FirstOrDefault(
            p => requestedModel.StartsWith(p.Prefix, StringComparison.Ordinal));

        if (provider is null)
        {
            await ForwardNativeAsync(context, options, httpClient, requestBody, cancellationToken);
            return;
        }

        var upstreamModel = requestedModel[provider.Prefix.Length..];
        if (string.IsNullOrWhiteSpace(upstreamModel))
        {
            await ProxyHttp.WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Model prefix was provided without an upstream model id.");
            return;
        }

        root["model"] = upstreamModel;
        var upstreamUri = ProxyHttp.BuildEndpointUri(provider.BaseUrl, "responses", context.Request.QueryString.Value);

        using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUri)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(root.ToJsonString()))
        };

        ProxyHttp.CopyRequestHeaders(context.Request, request, externalProvider: true, includeAcceptEncoding: true);
        request.Content.Headers.ContentType ??= new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        await ProxyHttp.CopyUpstreamResponseAsync(context, response, cancellationToken);
    }

    private static byte[] DecodeRequestBody(HttpRequest request, byte[] requestBody)
    {
        var encodings = request.Headers.ContentEncoding
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (encodings.Length == 0)
        {
            return requestBody;
        }

        if (encodings.Length != 1 || !encodings[0].Equals("zstd", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Unsupported request Content-Encoding: {string.Join(", ", encodings)}.");
        }

        try
        {
            using var decompressor = new Decompressor();
            return decompressor.Unwrap(requestBody).ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Request body contains invalid zstd data.", ex);
        }
    }

    private static async Task ForwardNativeAsync(
        HttpContext context,
        GatewayOptions options,
        HttpClient httpClient,
        byte[] requestBody,
        CancellationToken cancellationToken)
    {
        var upstreamUri = ProxyHttp.BuildEndpointUri(options.NativeBaseUrl, "responses", context.Request.QueryString.Value);

        using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUri)
        {
            Content = new ByteArrayContent(requestBody)
        };

        ProxyHttp.CopyRequestHeaders(context.Request, request, externalProvider: false, includeAcceptEncoding: true);
        request.Content.Headers.ContentType ??= new MediaTypeHeaderValue("application/json");

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        await ProxyHttp.CopyUpstreamResponseAsync(context, response, cancellationToken);
    }
}
