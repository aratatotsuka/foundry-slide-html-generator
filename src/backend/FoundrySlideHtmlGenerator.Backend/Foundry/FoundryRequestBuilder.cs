using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoundrySlideHtmlGenerator.Backend.Foundry;

public static class FoundryRequestBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonDocument BuildJsonSchemaResponseRequest(
        string model,
        string instructions,
        object input,
        object[] tools,
        string schemaName,
        JsonElement schema)
    {
        // OpenAI /responses structured outputs (json_schema)
        // Ref: https://platform.openai.com/docs/api-reference/responses/create
        var body = new
        {
            model,
            instructions,
            input,
            tools,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = schemaName,
                    strict = true,
                    schema
                }
            }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(body, JsonOptions));
    }

    public static JsonDocument BuildTextResponseRequest(
        string model,
        string instructions,
        object input,
        object[] tools)
    {
        var body = new
        {
            model,
            instructions,
            input,
            tools
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(body, JsonOptions));
    }

    public static object BuildUserInput(string text, string? imageDataUrl)
    {
        if (string.IsNullOrWhiteSpace(imageDataUrl))
        {
            return new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text }
                    }
                }
            };
        }

        return new[]
        {
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "input_text", text },
                    new { type = "input_image", image_url = imageDataUrl }
                }
            }
        };
    }
}

