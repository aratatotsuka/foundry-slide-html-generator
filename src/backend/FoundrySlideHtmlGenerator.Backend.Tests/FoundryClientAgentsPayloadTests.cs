using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using FoundrySlideHtmlGenerator.Backend.Foundry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FoundrySlideHtmlGenerator.Backend.Tests;

public sealed class FoundryClientAgentsPayloadTests
{
    [Fact]
    public async Task CreateAgentAsync_SendsNameAndPromptDefinitionKind()
    {
        var handler = new CaptureHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"id\":\"agent_123\"}", Encoding.UTF8, "application/json")
            }
        };

        var client = CreateClient(handler);

        var definition = new AgentDefinition(
            Name: "agent-planner",
            Instructions: "hello",
            Tools: [new { type = "web_search_preview" }]);

        _ = await client.CreateAgentAsync(definition, CancellationToken.None);

        Assert.NotNull(handler.LastBody);

        using var json = JsonDocument.Parse(handler.LastBody!);
        var root = json.RootElement;

        Assert.Equal("agent-planner", root.GetProperty("name").GetString());

        var def = root.GetProperty("definition");
        Assert.Equal("prompt", def.GetProperty("kind").GetString());
        Assert.Equal("model", def.GetProperty("model").GetString());
        Assert.Equal("hello", def.GetProperty("instructions").GetString());
        Assert.False(def.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task UpdateAgentAsync_SendsPromptDefinitionKind_WithoutRootName()
    {
        var handler = new CaptureHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        };

        var client = CreateClient(handler);

        var definition = new AgentDefinition(
            Name: "agent-planner",
            Instructions: "hello",
            Tools: []);

        await client.UpdateAgentAsync("agent-planner", definition, CancellationToken.None);

        Assert.NotNull(handler.LastBody);

        using var json = JsonDocument.Parse(handler.LastBody!);
        var root = json.RootElement;

        Assert.False(root.TryGetProperty("name", out _));

        var def = root.GetProperty("definition");
        Assert.Equal("prompt", def.GetProperty("kind").GetString());
        Assert.Equal("model", def.GetProperty("model").GetString());
        Assert.Equal("hello", def.GetProperty("instructions").GetString());
    }

    private static FoundryClient CreateClient(CaptureHandler handler)
    {
        var httpClient = new HttpClient(handler);

        var options = Options.Create(new FoundryOptions
        {
            ProjectEndpoint = "https://example.invalid/api/projects/x",
            ApiVersion = "2025-11-15-preview",
            ModelDeploymentName = "model",
            HttpTimeoutSeconds = 10
        });

        return new FoundryClient(
            httpClient,
            options,
            new FakeTokenCredential(),
            NullLogger<FoundryClient>.Instance);
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; init; }
            = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return Responder(request);
        }
    }
}
