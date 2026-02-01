using System.Text.Json;
using FoundrySlideHtmlGenerator.Backend.Contracts;
using FoundrySlideHtmlGenerator.Backend.Foundry;
using FoundrySlideHtmlGenerator.Backend.Jobs;
using FoundrySlideHtmlGenerator.Backend.Orchestration;
using FoundrySlideHtmlGenerator.Backend.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FoundrySlideHtmlGenerator.Backend.Tests;

public sealed class ValidatorLoopTests
{
    [Fact]
    public async Task ValidatorFailure_ReGeneratesUpToTwoTimes()
    {
        var foundry = new FakeFoundryClient();
        var resources = new FoundryResourceContext();
        resources.MarkReady();

        var jobStore = new InMemoryJobStore(new JobInput
        {
            Prompt = "Test prompt",
            Aspect = "16:9",
            ImageDataUrl = null
        });

        var renderer = new FakePngRenderer();
        var options = Options.Create(new FoundryOptions
        {
            ProjectEndpoint = "https://example.invalid/api/projects/x",
            ApiVersion = "2025-11-15-preview",
            ModelDeploymentName = "model"
        });

        var orchestrator = new SlideGenerationOrchestrator(
            foundry,
            resources,
            jobStore,
            renderer,
            options,
            NullLogger<SlideGenerationOrchestrator>.Instance);

        await jobStore.CreateAsync("job1", new GenerateRequest { Prompt = "Test prompt", Aspect = "16:9" }, imageDataUrl: null, CancellationToken.None);
        await orchestrator.RunAsync(new JobWorkItem("job1"), CancellationToken.None);

        Assert.Equal(2, foundry.HtmlGeneratorCalls);
        Assert.Equal(2, foundry.ValidatorCalls);

        var state = await jobStore.GetAsync("job1", CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal(JobStatus.Succeeded, state!.Status);

        Assert.NotNull(jobStore.LastHtml);
        Assert.DoesNotContain("<script", jobStore.LastHtml!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(jobStore.LastPng);
        Assert.True(jobStore.LastPng!.Length > 0);
    }

    private sealed class FakePngRenderer : IPngRenderer
    {
        public Task<byte[]> RenderAsync(string html, string aspect, CancellationToken cancellationToken)
            => Task.FromResult(new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // minimal PNG signature prefix
    }

    private sealed class InMemoryJobStore : IJobStore
    {
        private JobState? _state;
        private readonly JobInput _input;

        public InMemoryJobStore(JobInput input) => _input = input;

        public string? LastHtml { get; private set; }
        public byte[]? LastPng { get; private set; }

        public Task CreateAsync(string jobId, GenerateRequest request, string? imageDataUrl, CancellationToken cancellationToken)
        {
            _state = new JobState { JobId = jobId };
            return Task.CompletedTask;
        }

        public Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken)
            => Task.FromResult(_state);

        public Task<JobInput?> GetInputAsync(string jobId, CancellationToken cancellationToken)
            => Task.FromResult<JobInput?>(_input);

        public Task UpdateAsync(string jobId, Action<JobState> mutate, CancellationToken cancellationToken)
        {
            if (_state is null) _state = new JobState { JobId = jobId };
            mutate(_state);
            return Task.CompletedTask;
        }

        public Task SaveHtmlAsync(string jobId, string html, CancellationToken cancellationToken)
        {
            LastHtml = html;
            if (_state is not null) _state.ResultHtmlPath = "memory://result.html";
            return Task.CompletedTask;
        }

        public Task SavePreviewPngAsync(string jobId, byte[] pngBytes, CancellationToken cancellationToken)
        {
            LastPng = pngBytes;
            if (_state is not null) _state.PreviewPngPath = "memory://preview.png";
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFoundryClient : IFoundryClient
    {
        public int HtmlGeneratorCalls { get; private set; }
        public int ValidatorCalls { get; private set; }

        public Task<IReadOnlyDictionary<string, string>> ListAgentsByNameAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<JsonDocument> GetAgentAsync(string agentId, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<string> CreateAgentAsync(AgentDefinition definition, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task UpdateAgentAsync(string agentId, AgentDefinition definition, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<string> UploadFileAsync(string filePath, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<string> CreateVectorStoreAsync(string name, IReadOnlyList<string> fileIds, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task WaitForVectorStoreReadyAsync(string vectorStoreId, TimeSpan timeout, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<JsonDocument> CreateResponseAsync(JsonDocument requestBody, CancellationToken cancellationToken)
        {
            var instructions = requestBody.RootElement.GetProperty("instructions").GetString() ?? "";

            if (instructions == Instructions.Planner)
            {
                return Task.FromResult(OutputText(JsonSerializer.Serialize(new
                {
                    slideCount = 5,
                    slideOutline = new[]
                    {
                        new { title = "S1", bullets = new[] { "a", "b", "c" } },
                        new { title = "S2", bullets = new[] { "a", "b", "c" } },
                        new { title = "S3", bullets = new[] { "a", "b", "c" } },
                        new { title = "S4", bullets = new[] { "a", "b", "c" } },
                        new { title = "S5", bullets = new[] { "a", "b", "c" } }
                    },
                    searchQueries = new[] { "q1", "q2", "q3" },
                    keyConstraints = new[] { "no-script" }
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            }

            if (instructions == Instructions.WebResearch)
            {
                return Task.FromResult(OutputText(JsonSerializer.Serialize(new
                {
                    findings = Array.Empty<string>(),
                    citations = Array.Empty<object>(),
                    usedQueries = Array.Empty<string>()
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            }

            if (instructions == Instructions.HtmlGenerator)
            {
                HtmlGeneratorCalls++;
                var html = HtmlGeneratorCalls == 1
                    ? "<html><head><script>bad()</script></head><body><section class=\"slide\"></section></body></html>"
                    : "<html><head></head><body><section class=\"slide\"></section></body></html>";
                return Task.FromResult(OutputText(html));
            }

            if (instructions == Instructions.Validator)
            {
                ValidatorCalls++;
                if (ValidatorCalls == 1)
                {
                    return Task.FromResult(OutputText(JsonSerializer.Serialize(new
                    {
                        ok = false,
                        issues = new[] { "Contains <script> tag" },
                        fixedPromptAppendix = "Remove all <script> tags."
                    }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
                }

                return Task.FromResult(OutputText(JsonSerializer.Serialize(new
                {
                    ok = true,
                    issues = Array.Empty<string>()
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            }

            throw new InvalidOperationException("Unexpected instructions.");
        }

        private static JsonDocument OutputText(string text)
            => JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                output_text = text
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

