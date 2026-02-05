using System.Text.Json;
using FoundrySlideHtmlGenerator.Backend.Foundry;

namespace FoundrySlideHtmlGenerator.Backend.Tests;

public sealed class FoundryRequestBuilderTests
{
    [Fact]
    public void BuildCreateConversationRequest_IncludesImageWhenProvided()
    {
        using var doc = FoundryRequestBuilder.BuildCreateConversationRequest(
            initialUserText: "hi",
            imageDataUrl: "data:image/png;base64,AAAA",
            metadata: null);

        var content = doc.RootElement.GetProperty("items")[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        Assert.Equal("input_image", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AAAA", content[1].GetProperty("image_url").GetString());
    }

    [Fact]
    public void BuildUserInput_IncludesImageWhenProvided()
    {
        var input = FoundryRequestBuilder.BuildUserInput("hi", "data:image/png;base64,AAAA");
        var json = JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var content = json[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        Assert.Equal("input_image", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AAAA", content[1].GetProperty("image_url").GetString());
    }

    [Fact]
    public void BuildJsonSchemaResponseRequest_WritesExpectedShape()
    {
        using var doc = FoundryRequestBuilder.BuildJsonSchemaResponseRequest(
            model: "my-model",
            instructions: "system",
            input: FoundryRequestBuilder.BuildUserInput("hello", imageDataUrl: null),
            tools: new object[] { new { type = "web_search_preview" } },
            schemaName: "planner",
            schema: JsonSchemas.PlannerSchema);

        var root = doc.RootElement;
        Assert.Equal("my-model", root.GetProperty("model").GetString());
        Assert.Equal("system", root.GetProperty("instructions").GetString());

        var textFormat = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", textFormat.GetProperty("type").GetString());
        Assert.Equal("planner", textFormat.GetProperty("name").GetString());
        Assert.True(textFormat.GetProperty("strict").GetBoolean());
        Assert.Equal("object", textFormat.GetProperty("schema").GetProperty("type").GetString());
    }
}
