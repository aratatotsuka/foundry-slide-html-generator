using System.Text.Json;

namespace FoundrySlideHtmlGenerator.Backend.Foundry;

public static class FoundryResponseParser
{
    public static string ExtractOutputText(JsonDocument response)
    {
        var root = response.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            {
                return outputText.GetString() ?? "";
            }

            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var item in output.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var contentItem in content.EnumerateArray())
                    {
                        if (!contentItem.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var type = typeProp.GetString();
                        if (string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase) &&
                            contentItem.TryGetProperty("text", out var textProp) &&
                            textProp.ValueKind == JsonValueKind.String)
                        {
                            parts.Add(textProp.GetString() ?? "");
                        }
                    }
                }

                if (parts.Count > 0)
                {
                    return string.Join("\n", parts);
                }
            }
        }

        return "";
    }

    public static T ParseJsonFromOutputText<T>(JsonDocument response, JsonSerializerOptions? options = null)
    {
        var text = ExtractOutputText(response);
        text = StripCodeFences(text).Trim();
        return JsonSerializer.Deserialize<T>(text, options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new JsonException("Failed to deserialize JSON from output text.");
    }

    public static string StripCodeFences(string text)
    {
        // Some models occasionally wrap JSON/HTML in markdown fences even when asked not to.
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
            {
                text = text[(firstNewline + 1)..];
            }

            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
            {
                text = text[..lastFence];
            }
        }

        return text;
    }
}

