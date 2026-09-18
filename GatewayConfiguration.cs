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
