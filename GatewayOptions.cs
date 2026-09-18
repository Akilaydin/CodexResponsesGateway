using System.Text.Json.Serialization;

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
