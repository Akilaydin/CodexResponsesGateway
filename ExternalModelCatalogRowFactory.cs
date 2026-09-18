using System.Text.Json.Nodes;

static class ExternalModelCatalogRowFactory
{
    public static JsonObject Create(
        JsonObject schemaTemplate,
        JsonObject? matchingNativeModel,
        ProviderOptions provider,
        string upstreamModel)
    {
        var external = (matchingNativeModel ?? schemaTemplate).DeepClone().AsObject();

        if (matchingNativeModel is null)
        {
            ApplyUnknownModelFallback(external);
        }

        if (provider.ModelOverrides.TryGetValue(upstreamModel, out var modelOverride))
        {
            ApplyModelOverride(external, modelOverride);
        }

        external["slug"] = provider.Prefix + upstreamModel;
        external["display_name"] = $"{provider.Id} — {upstreamModel}";
        external["description"] = $"{upstreamModel} via {provider.Id}";
        external["visibility"] = "list";
        external["supported_in_api"] = true;
        external["prefer_websockets"] = false;
        return external;
    }

    private static void ApplyModelOverride(JsonObject model, ModelOverrideOptions modelOverride)
    {
        if (modelOverride.ReasoningLevels is null)
        {
            return;
        }

        var levels = new JsonArray();
        foreach (var level in modelOverride.ReasoningLevels)
        {
            levels.Add((JsonNode)new JsonObject
            {
                ["effort"] = level,
                ["description"] = $"{level} reasoning effort"
            });
        }

        model["supported_reasoning_levels"] = levels;
        model["default_reasoning_level"] = modelOverride.DefaultReasoningLevel;
    }

    private static void ApplyUnknownModelFallback(JsonObject model)
    {
        // Mirrors Codex's conservative unknown-model metadata while retaining any
        // future schema fields from the cloned native row.
        model["default_reasoning_level"] = null;
        model["supported_reasoning_levels"] = new JsonArray();
        model["shell_type"] = "unified_exec";
        model["priority"] = 99;
        model["additional_speed_tiers"] = new JsonArray();
        model["service_tiers"] = new JsonArray();
        model["default_service_tier"] = null;
        model["available_access_programs"] = null;
        model["availability_nux"] = null;
        model["upgrade"] = null;
        model["include_skills_usage_instructions"] = false;
        model["include_plugin_usage_instructions"] = false;
        model["include_apps_usage_instructions"] = false;
        model["supports_reasoning_summary_parameter"] = true;
        model["default_reasoning_summary"] = "auto";
        model["support_verbosity"] = false;
        model["default_verbosity"] = null;
        model["apply_patch_tool_type"] = null;
        model["web_search_tool_type"] = "text";
        model["truncation_policy"] = new JsonObject
        {
            ["mode"] = "bytes",
            ["limit"] = 10_000
        };
        model["supports_image_detail_original"] = false;
        model["context_window"] = 272_000;
        model["max_context_window"] = 272_000;
        model["auto_compact_token_limit"] = null;
        model["comp_hash"] = null;
        model["effective_context_window_percent"] = 95;
        model["experimental_supported_tools"] = new JsonArray();
        model["input_modalities"] = new JsonArray("text", "image");
        model["supports_search_tool"] = false;
        model["supports_experimental_context"] = false;
        model["use_responses_lite"] = false;
        model["guardian"] = null;
        model["node_repl_auto_review_required"] = false;
        model["node_repl_disabled"] = false;
        model["auto_review_model_override"] = null;
        model["model_specialty"] = null;
        model["tool_mode"] = null;
        model["multi_agent_version"] = null;
        model["multi_agent_reasoning_effort"] = null;
    }
}
