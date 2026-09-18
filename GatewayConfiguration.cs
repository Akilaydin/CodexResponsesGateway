using System.Text.Json;

static class GatewayConfiguration
{
    private const string LocalConfigFileName = "gateway.local.json";

    public static GatewayOptions Load()
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
        foreach (var provider in options.Providers)
        {
            provider.ExcludedModels ??= [];
            provider.ModelOverrides ??= [];
        }
        return options;
    }

    public static void Validate(GatewayOptions options)
    {
        ValidateHttpUrl(options.ListenUrl, "listenUrl");
        ValidateHttpUrl(options.NativeBaseUrl, "nativeBaseUrl");

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

            ValidateHttpUrl(provider.BaseUrl, $"Provider {provider.Id} baseUrl");

            if (string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                throw new InvalidOperationException($"Provider {provider.Id} apiKey must not be empty.");
            }

            ValidateModelOverrides(provider);
        }
    }

    private static void ValidateModelOverrides(ProviderOptions provider)
    {
        foreach (var (modelId, modelOverride) in provider.ModelOverrides)
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                throw new InvalidOperationException($"Provider {provider.Id} has an empty modelOverrides key.");
            }

            if (modelOverride.ReasoningLevels is null)
            {
                if (modelOverride.DefaultReasoningLevel is not null)
                {
                    throw new InvalidOperationException(
                        $"Provider {provider.Id} model {modelId} sets defaultReasoningLevel without reasoningLevels.");
                }

                continue;
            }

            var levels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var level in modelOverride.ReasoningLevels)
            {
                if (string.IsNullOrWhiteSpace(level) || !levels.Add(level))
                {
                    throw new InvalidOperationException(
                        $"Provider {provider.Id} model {modelId} has empty or duplicate reasoningLevels.");
                }
            }

            if (modelOverride.DefaultReasoningLevel is not null
                && !levels.Contains(modelOverride.DefaultReasoningLevel))
            {
                throw new InvalidOperationException(
                    $"Provider {provider.Id} model {modelId} defaultReasoningLevel must be present in reasoningLevels.");
            }
        }
    }

    private static void ValidateHttpUrl(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{name} must be an absolute HTTP(S) URL.");
        }
    }
}
