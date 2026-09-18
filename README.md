# CodexResponsesGateway

Small local gateway for adding custom OpenAI Responses-compatible models to Codex Desktop without losing the native Codex model catalog.

## What it does

- preserves native Codex models and routes them to the official backend;
- discovers models from one or more external `GET /v1/models` providers;
- adds external models to the Codex picker under configurable prefixes;
- routes external `POST /v1/responses` requests to the configured provider;
- supports per-model reasoning-level metadata for the Codex UI;

Only OpenAI Responses-compatible providers are supported. 
The gateway does not translate Chat Completions, Anthropic Messages, Gemini native APIs, or other protocols.

## Requirements

- .NET 10 SDK
- Codex Desktop
- an external provider supporting `GET /v1/models` and `POST /v1/responses`

## Configuration

Copy `gateway.example.json` to `gateway.local.json` and configure your provider:

```json
{
  "listenUrl": "http://127.0.0.1:8320",
  "providers": [
    {
      "id": "example",
      "prefix": "example/",
      "baseUrl": "https://example.com/v1",
      "apiKey": "<API_KEY>",
      "excludedModels": [],
      "modelOverrides": {
        "example-model": {
          "reasoningLevels": ["low", "medium", "high"],
          "defaultReasoningLevel": "medium"
        }
      }
    }
  ]
}
```

Then point Codex to the gateway at root level in `config.toml`:

```toml
openai_base_url = "http://127.0.0.1:8320/v1"
```

The prefix exists only for routing inside the gateway. For example, `example/claude-sonnet-4-6` is sent upstream as `claude-sonnet-4-6`.

## Build and run

```powershell
dotnet build
dotnet publish -c Release -r win-x64 -o .\publish
.\publish\CodexResponsesGateway.exe
```


`gateway.local.json`, publish output, and secrets are ignored by git.
