using System.Text.Json;

namespace FoundrySlideHtmlGenerator.Backend.Foundry;

public static class JsonSchemas
{
    private static readonly JsonDocument PlannerDoc = JsonDocument.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["slideCount", "slideOutline", "searchQueries", "keyConstraints"],
          "properties": {
            "slideCount": { "type": "integer", "minimum": 1, "maximum": 1 },
            "slideOutline": {
              "type": "array",
              "minItems": 1,
              "maxItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["title", "bullets"],
                "properties": {
                  "title": { "type": "string", "minLength": 1 },
                  "bullets": { "type": "array", "minItems": 3, "maxItems": 6, "items": { "type": "string" } }
                }
              }
            },
            "searchQueries": { "type": "array", "minItems": 0, "maxItems": 8, "items": { "type": "string" } },
            "keyConstraints": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

    private static readonly JsonDocument WebResearchDoc = JsonDocument.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["findings", "citations", "usedQueries"],
          "properties": {
            "findings": { "type": "array", "items": { "type": "string" } },
            "citations": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["title", "url", "quote"],
                "properties": {
                  "title": { "type": "string" },
                  "url": { "type": "string" },
                  "quote": { "type": "string" }
                }
              }
            },
            "usedQueries": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

    private static readonly JsonDocument FileResearchDoc = JsonDocument.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["snippets", "fileCitations"],
          "properties": {
            "snippets": { "type": "array", "items": { "type": "string" } },
            "fileCitations": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["fileId", "filename", "snippet"],
                "properties": {
                  "fileId": { "type": "string" },
                  "filename": { "type": "string" },
                  "snippet": { "type": "string" }
                }
              }
            }
          }
        }
        """);

    private static readonly JsonDocument ValidatorDoc = JsonDocument.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["ok", "issues", "fixedPromptAppendix"],
          "properties": {
            "ok": { "type": "boolean" },
            "issues": { "type": "array", "items": { "type": "string" } },
            "fixedPromptAppendix": { "type": "string" }
          }
        }
        """);

    public static JsonElement PlannerSchema => PlannerDoc.RootElement;
    public static JsonElement WebResearchSchema => WebResearchDoc.RootElement;
    public static JsonElement FileResearchSchema => FileResearchDoc.RootElement;
    public static JsonElement ValidatorSchema => ValidatorDoc.RootElement;
}
