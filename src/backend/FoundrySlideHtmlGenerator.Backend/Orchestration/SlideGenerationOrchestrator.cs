using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FoundrySlideHtmlGenerator.Backend.Foundry;
using FoundrySlideHtmlGenerator.Backend.Jobs;
using FoundrySlideHtmlGenerator.Backend.Rendering;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Options;

namespace FoundrySlideHtmlGenerator.Backend.Orchestration;

// Multi-agent orchestration (required):
// Plan -> Research(Web) -> Research(File) -> Generate HTML -> Validate (fix loop up to 2x) -> Render PNG
public sealed class SlideGenerationOrchestrator
{
    private readonly IFoundryClient _foundry;
    private readonly FoundryResourceContext _resources;
    private readonly IJobStore _jobs;
    private readonly IPngRenderer _renderer;
    private readonly FoundryOptions _options;
    private readonly ILogger<SlideGenerationOrchestrator> _logger;

    public SlideGenerationOrchestrator(
        IFoundryClient foundry,
        FoundryResourceContext resources,
        IJobStore jobs,
        IPngRenderer renderer,
        IOptions<FoundryOptions> options,
        ILogger<SlideGenerationOrchestrator> logger)
    {
        _foundry = foundry;
        _resources = resources;
        _jobs = jobs;
        _renderer = renderer;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(JobWorkItem workItem, CancellationToken cancellationToken)
    {
        await _resources.Ready.WaitAsync(cancellationToken);

        var input = await _jobs.GetInputAsync(workItem.JobId, cancellationToken);
        if (input is null)
        {
            throw new InvalidOperationException("Job input not found.");
        }

        await _jobs.UpdateAsync(workItem.JobId, state =>
        {
            state.Status = JobStatus.Running;
            state.Step = JobSteps.Plan;
            state.Error = null;
        }, cancellationToken);
        _logger.LogInformation("Step {Step}", JobSteps.Plan);

        var effectivePrompt = AspectPrompt.ComposeEffectivePrompt(input.Prompt, input.Aspect);

        if (_options.UseFoundryWorkflow)
        {
            try
            {
                await _jobs.UpdateAsync(workItem.JobId, state => state.Step = JobSteps.Plan, cancellationToken);
                _logger.LogInformation("Step {Step} (Foundry Workflow)", JobSteps.Plan);

                var foundryWorkflowHtml = await RunFoundryWorkflowAsync(
                    workItem.JobId,
                    effectivePrompt,
                    input.Aspect,
                    input.ImageDataUrl,
                    cancellationToken);

                await _jobs.SaveHtmlAsync(workItem.JobId, foundryWorkflowHtml, cancellationToken);

                var slideCount = CountSlides(foundryWorkflowHtml);
                if (slideCount != 1)
                {
                    throw new InvalidOperationException($"Foundry workflow returned invalid slide count: expected 1 but found {slideCount}.");
                }

                var foundryWorkflowPng = await _renderer.RenderAsync(foundryWorkflowHtml, input.Aspect, cancellationToken);
                await _jobs.SavePreviewPngAsync(workItem.JobId, foundryWorkflowPng, cancellationToken);

                await _jobs.UpdateAsync(workItem.JobId, state =>
                {
                    state.Status = JobStatus.Succeeded;
                    state.Step = null;
                    state.Error = null;
                }, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry workflow orchestration failed. Falling back to in-process/imperative orchestration.");
            }
        }

        if (_options.UseWorkflows)
        {
            try
            {
                _logger.LogInformation("Running in-process workflow orchestration (Microsoft Agent Framework Workflows).");

                var workflowHtml = await RunWorkflowAsync(
                    workItem.JobId,
                    effectivePrompt,
                    input.Aspect,
                    input.ImageDataUrl,
                    cancellationToken);

                await _jobs.SaveHtmlAsync(workItem.JobId, workflowHtml, cancellationToken);

                var slideCount = CountSlides(workflowHtml);
                if (slideCount != 1)
                {
                    throw new InvalidOperationException($"Workflow returned invalid slide count: expected 1 but found {slideCount}.");
                }

                var workflowPng = await _renderer.RenderAsync(workflowHtml, input.Aspect, cancellationToken);
                await _jobs.SavePreviewPngAsync(workItem.JobId, workflowPng, cancellationToken);

                await _jobs.UpdateAsync(workItem.JobId, state =>
                {
                    state.Status = JobStatus.Succeeded;
                    state.Step = null;
                    state.Error = null;
                }, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workflow orchestration failed. Falling back to non-workflow orchestration.");
            }
        }

        if (_options.UseConnectedAgents)
        {
            try
            {
                await _jobs.UpdateAsync(workItem.JobId, state => state.Step = JobSteps.Plan, cancellationToken);
                _logger.LogInformation("Step {Step} (Connected Agents)", JobSteps.Plan);

                var connectedHtml = await RunConnectedOrchestratorAsync(
                    workItem.JobId,
                    effectivePrompt,
                    input.Aspect,
                    cancellationToken);

                await _jobs.SaveHtmlAsync(workItem.JobId, connectedHtml, cancellationToken);

                var slideCount = CountSlides(connectedHtml);
                if (slideCount != 1)
                {
                    throw new InvalidOperationException($"Connected Agents returned invalid slide count: expected 1 but found {slideCount}.");
                }

                var connectedPng = await _renderer.RenderAsync(connectedHtml, input.Aspect, cancellationToken);
                await _jobs.SavePreviewPngAsync(workItem.JobId, connectedPng, cancellationToken);

                await _jobs.UpdateAsync(workItem.JobId, state =>
                {
                    state.Status = JobStatus.Succeeded;
                    state.Step = null;
                    state.Error = null;
                }, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connected Agents orchestration failed. Falling back to client-side orchestration.");
            }
        }

        // 1) Planner
        PlannerOutput planner;
        try
        {
            planner = await RunPlannerAsync(effectivePrompt, input.ImageDataUrl, cancellationToken);
            planner = NormalizePlannerOutput(planner, input.Prompt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Planner failed. Falling back to a local outline.");
            planner = BuildFallbackPlannerOutput(input.Prompt);
        }

        // 2/3) Research (web + file). Run in parallel where possible.
        var webTask = RunWebResearchSafeAsync(planner.SearchQueries, cancellationToken);
        var fileTask = string.IsNullOrWhiteSpace(_resources.VectorStoreId)
            ? Task.FromResult(EmptyFileResearch())
            : RunFileResearchSafeAsync(effectivePrompt, planner, cancellationToken);

        await _jobs.UpdateAsync(workItem.JobId, state => state.Step = JobSteps.ResearchWeb, cancellationToken);
        _logger.LogInformation("Step {Step}", JobSteps.ResearchWeb);
        var webResearch = await webTask;
        await AddWebSourcesAsync(workItem.JobId, webResearch, cancellationToken);

        await _jobs.UpdateAsync(workItem.JobId, state => state.Step = JobSteps.ResearchFile, cancellationToken);
        _logger.LogInformation("Step {Step}", JobSteps.ResearchFile);
        var fileResearch = await fileTask;
        await AddFileSourcesAsync(workItem.JobId, fileResearch, cancellationToken);

        // 4/5) Generate + validate loop
        var constraints = AspectPrompt.ValidatorConstraintsFor(input.Aspect);
        var html = await GenerateWithValidationLoopAsync(
            workItem.JobId,
            effectivePrompt,
            input.Aspect,
            input.ImageDataUrl,
            planner,
            webResearch,
            fileResearch,
            constraints,
            cancellationToken);

        // Render PNG (preview only; HTML never returned to frontend)
        var png = await _renderer.RenderAsync(html, input.Aspect, cancellationToken);
        await _jobs.SavePreviewPngAsync(workItem.JobId, png, cancellationToken);

        await _jobs.UpdateAsync(workItem.JobId, state =>
        {
            state.Status = JobStatus.Succeeded;
            state.Step = null;
            state.Error = null;
        }, cancellationToken);
    }

    private async Task<string> RunConnectedOrchestratorAsync(string jobId, string effectivePrompt, string aspect, CancellationToken cancellationToken)
    {
        if (!_resources.AssistantIds.TryGetValue(AssistantNames.Planner, out var assistantId) || string.IsNullOrWhiteSpace(assistantId))
        {
            throw new InvalidOperationException($"Connected Agents is enabled but assistant id is missing for '{AssistantNames.Planner}'.");
        }

        var (w, h, safe) = AspectPrompt.GetCanvas(aspect);
        var constraints = AspectPrompt.ValidatorConstraintsFor(aspect);

        var prompt = new StringBuilder();
        prompt.AppendLine("USER PROMPT (with server-side constraints):");
        prompt.AppendLine(effectivePrompt);
        prompt.AppendLine();
        prompt.AppendLine("CANVAS:");
        prompt.AppendLine($"- {w}x{h}px (safe margin {safe}px)");
        prompt.AppendLine();
        prompt.AppendLine("VALIDATION CONSTRAINTS:");
        prompt.AppendLine(constraints);

        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            assistant_id = assistantId,
            tool_choice = "required",
            thread = new
            {
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new { text = prompt.ToString() }
                    }
                }
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        using var created = await _foundry.CreateThreadAndRunAsync(request, cancellationToken);
        if (!created.RootElement.TryGetProperty("id", out var runIdProp) || runIdProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("CreateThreadAndRunAsync: response missing run id.");
        }
        if (!created.RootElement.TryGetProperty("thread_id", out var threadIdProp) || threadIdProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("CreateThreadAndRunAsync: response missing thread id.");
        }

        var runId = runIdProp.GetString();
        var threadId = threadIdProp.GetString();
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("CreateThreadAndRunAsync: response missing run/thread id.");
        }

        await WaitForRunCompletedAsync(threadId!, runId!, cancellationToken);

        using var messages = await _foundry.ListMessagesAsync(threadId!, limit: 20, order: "desc", cancellationToken);
        var html = ExtractLatestAssistantText(messages);
        html = FoundryResponseParser.StripCodeFences(html).Trim();

        if (string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidOperationException("Connected Agents returned empty output.");
        }

        await AddWebSourcesFromAssistantMessagesAsync(jobId, messages, cancellationToken);
        return html;
    }

    private async Task<string> RunFoundryWorkflowAsync(
        string jobId,
        string effectivePrompt,
        string aspect,
        string? imageDataUrl,
        CancellationToken cancellationToken)
    {
        var workflowName = _options.FoundryWorkflowName?.Trim();
        if (string.IsNullOrWhiteSpace(workflowName))
        {
            throw new InvalidOperationException("USE_FOUNDRY_WORKFLOW=true but FOUNDRY_WORKFLOW_NAME is empty.");
        }

        // Foundry portal Workflows are represented as Agents (WorkflowAgentDefinition),
        // and are invoked via /openai/responses with an AgentReference.
        if (!_resources.AgentIds.TryGetValue(workflowName, out var workflowAgentId) || string.IsNullOrWhiteSpace(workflowAgentId))
        {
            var existingAgents = await _foundry.ListAgentsByNameAsync(cancellationToken);
            if (!existingAgents.TryGetValue(workflowName, out workflowAgentId) || string.IsNullOrWhiteSpace(workflowAgentId))
            {
                throw new InvalidOperationException(
                    $"Foundry workflow agent '{workflowName}' not found. Ensure it exists in Foundry portal (Workflows) for the same project endpoint, then enable USE_FOUNDRY_WORKFLOW.");
            }

            _resources.AgentIds[workflowName] = workflowAgentId;
        }

        // The workflow YAML defaults to 16:9 unless the first user message contains "4:3".
        // Include it explicitly to avoid accidental mismatches.
        var workflowPrompt = aspect == "4:3"
            ? $"4:3\n{effectivePrompt}"
            : effectivePrompt;

        using var createConversation = FoundryRequestBuilder.BuildCreateConversationRequest(
            initialUserText: workflowPrompt,
            imageDataUrl: imageDataUrl,
            metadata: new Dictionary<string, string>
            {
                ["jobId"] = jobId,
                ["source"] = "backend"
            });
        var conversationId = await _foundry.CreateConversationAsync(createConversation, cancellationToken);

        using var request = FoundryRequestBuilder.BuildWorkflowAgentResponseRequest(
            agentName: workflowName,
            agentVersion: null,
            conversationId: conversationId);

        using var response = await _foundry.CreateProjectResponseAsync(request, cancellationToken);

        var html = FoundryResponseParser.ExtractOutputText(response);
        html = FoundryResponseParser.StripCodeFences(html).Trim();

        if (string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidOperationException("Foundry workflow returned empty output.");
        }

        return html;
    }

    private async Task WaitForRunCompletedAsync(string threadId, string runId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var run = await _foundry.GetRunAsync(threadId, runId, cancellationToken);
            if (!run.RootElement.TryGetProperty("status", out var statusProp) || statusProp.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("GetRunAsync: response missing status.");
            }

            var status = statusProp.GetString() ?? "";
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
            {
                var lastError = run.RootElement.TryGetProperty("last_error", out var err) ? err.GetRawText() : null;
                throw new InvalidOperationException($"Run {runId} ended with status '{status}'. last_error={lastError}");
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Run {runId} did not complete before the timeout.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static string ExtractLatestAssistantText(JsonDocument messages)
    {
        var root = messages.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var message in data.EnumerateArray())
        {
            if (!message.TryGetProperty("role", out var roleProp) || roleProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!string.Equals(roleProp.GetString(), "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (!string.Equals(typeProp.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!item.TryGetProperty("text", out var textObj) || textObj.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (textObj.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.String)
                {
                    var value = valueProp.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        parts.Add(value);
                    }
                }
            }

            if (parts.Count > 0)
            {
                return string.Join("\n", parts);
            }
        }

        return "";
    }

    private Task AddWebSourcesFromAssistantMessagesAsync(string jobId, JsonDocument messages, CancellationToken cancellationToken)
    {
        var urls = ExtractUrlsFromAssistantMessages(messages);
        if (urls.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _jobs.UpdateAsync(jobId, state =>
        {
            foreach (var url in urls)
            {
                state.Sources.Urls.Add(url);
            }
        }, cancellationToken);
    }

    private static HashSet<string> ExtractUrlsFromAssistantMessages(JsonDocument messages)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var root = messages.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return urls;
        }

        foreach (var message in data.EnumerateArray())
        {
            if (!message.TryGetProperty("role", out var roleProp) || roleProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!string.Equals(roleProp.GetString(), "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (!string.Equals(typeProp.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!item.TryGetProperty("text", out var textObj) || textObj.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!textObj.TryGetProperty("annotations", out var ann) || ann.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var a in ann.EnumerateArray())
                {
                    if (!a.TryGetProperty("type", out var annType) || annType.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (!string.Equals(annType.GetString(), "url_citation", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!a.TryGetProperty("url_citation", out var urlCitation) || urlCitation.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (urlCitation.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                    {
                        var url = urlProp.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            urls.Add(url);
                        }
                    }
                }
            }
        }

        return urls;
    }

    private async Task<string> RunWorkflowAsync(
        string jobId,
        string effectivePrompt,
        string aspect,
        string? imageDataUrl,
        CancellationToken cancellationToken)
    {
        var planner = ExecutorBindingExtensions.BindExecutor(new PlannerWorkflowExecutor(this, jobId, aspect, imageDataUrl));

        var workflowBuilder = new WorkflowBuilder(planner)
            .WithName("slide_generation")
            .WithDescription("Plan -> Web -> File -> Generate -> Validate (loop) -> Result");

        var web = ExecutorBindingExtensions.BindExecutor(new WebResearchWorkflowExecutor(this, jobId));
        var file = ExecutorBindingExtensions.BindExecutor(new FileResearchWorkflowExecutor(this, jobId));
        var generate = ExecutorBindingExtensions.BindExecutor(new GenerateHtmlWorkflowExecutor(this, jobId));
        var validate = ExecutorBindingExtensions.BindExecutor(new ValidateWorkflowExecutor(this, jobId));
        var result = ExecutorBindingExtensions.BindExecutor(new ResultWorkflowExecutor());

        workflowBuilder.BindExecutor(web);
        workflowBuilder.BindExecutor(file);
        workflowBuilder.BindExecutor(generate);
        workflowBuilder.BindExecutor(validate);
        workflowBuilder.BindExecutor(result);

        workflowBuilder.AddEdge(planner, web);
        workflowBuilder.AddEdge(web, file);
        workflowBuilder.AddEdge(file, generate);
        workflowBuilder.AddEdge(generate, validate);
        workflowBuilder.AddEdge<SlideWorkflowState>(validate, result, s => s!.Done);
        workflowBuilder.AddEdge<SlideWorkflowState>(validate, generate, s => !s!.Done);

        var workflow = workflowBuilder.Build(validateOrphans: true);

        string? lastHtml = null;
        await using var run = await InProcessExecution.StreamAsync(
            workflow,
            effectivePrompt,
            runId: $"slide_generation:{jobId}",
            cancellationToken);

        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            if (evt is WorkflowOutputEvent output
                && output.Is<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                lastHtml = text;
            }
            else if (evt is ExecutorCompletedEvent completed
                     && string.Equals(completed.ExecutorId, "result", StringComparison.OrdinalIgnoreCase)
                     && completed.Data is string resultText
                     && !string.IsNullOrWhiteSpace(resultText))
            {
                lastHtml = resultText;
            }
        }

        if (string.IsNullOrWhiteSpace(lastHtml))
        {
            throw new InvalidOperationException("Workflow produced no output.");
        }

        return FoundryResponseParser.StripCodeFences(lastHtml).Trim();
    }

    private sealed record SlideWorkflowState(
        string EffectivePrompt,
        string Aspect,
        string? ImageDataUrl,
        PlannerOutput Planner,
        WebResearchOutput Web,
        FileResearchOutput File,
        string Html,
        int Attempt,
        string? FixedAppendix,
        bool Done);

    private sealed class PlannerWorkflowExecutor : Executor<string, SlideWorkflowState>
    {
        private readonly SlideGenerationOrchestrator _owner;
        private readonly string _jobId;
        private readonly string _aspect;
        private readonly string? _imageDataUrl;

        public PlannerWorkflowExecutor(SlideGenerationOrchestrator owner, string jobId, string aspect, string? imageDataUrl)
            : base(id: "planner", options: ExecutorOptions.Default, declareCrossRunShareable: false)
        {
            _owner = owner;
            _jobId = jobId;
            _aspect = aspect;
            _imageDataUrl = imageDataUrl;
        }

        public override async ValueTask<SlideWorkflowState> HandleAsync(
            string effectivePrompt,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            await _owner._jobs.UpdateAsync(_jobId, s => s.Step = JobSteps.Plan, cancellationToken);
            _owner._logger.LogInformation("Step {Step} (workflow)", JobSteps.Plan);

            PlannerOutput planner;
            try
            {
                planner = await _owner.RunPlannerAsync(effectivePrompt, _imageDataUrl, cancellationToken);
                planner = NormalizePlannerOutput(planner, effectivePrompt);
            }
            catch (Exception ex)
            {
                _owner._logger.LogWarning(ex, "Planner failed (workflow). Falling back to a local outline.");
                planner = BuildFallbackPlannerOutput(effectivePrompt);
            }

            return new SlideWorkflowState(
                EffectivePrompt: effectivePrompt,
                Aspect: _aspect,
                ImageDataUrl: _imageDataUrl,
                Planner: planner,
                Web: EmptyWebResearch(),
                File: EmptyFileResearch(),
                Html: "",
                Attempt: 0,
                FixedAppendix: null,
                Done: false);
        }
    }

    private sealed class WebResearchWorkflowExecutor : Executor<SlideWorkflowState, SlideWorkflowState>
    {
        private readonly SlideGenerationOrchestrator _owner;
        private readonly string _jobId;

        public WebResearchWorkflowExecutor(SlideGenerationOrchestrator owner, string jobId)
            : base(id: "web_research", options: ExecutorOptions.Default, declareCrossRunShareable: false)
        {
            _owner = owner;
            _jobId = jobId;
        }

        public override async ValueTask<SlideWorkflowState> HandleAsync(
            SlideWorkflowState state,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            await _owner._jobs.UpdateAsync(_jobId, s => s.Step = JobSteps.ResearchWeb, cancellationToken);
            _owner._logger.LogInformation("Step {Step} (workflow)", JobSteps.ResearchWeb);

            var web = await _owner.RunWebResearchSafeAsync(state.Planner.SearchQueries, cancellationToken);
            await _owner.AddWebSourcesAsync(_jobId, web, cancellationToken);

            return state with { Web = web };
        }
    }

    private sealed class FileResearchWorkflowExecutor : Executor<SlideWorkflowState, SlideWorkflowState>
    {
        private readonly SlideGenerationOrchestrator _owner;
        private readonly string _jobId;

        public FileResearchWorkflowExecutor(SlideGenerationOrchestrator owner, string jobId)
            : base(id: "file_research", options: ExecutorOptions.Default, declareCrossRunShareable: false)
        {
            _owner = owner;
            _jobId = jobId;
        }

        public override async ValueTask<SlideWorkflowState> HandleAsync(
            SlideWorkflowState state,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            await _owner._jobs.UpdateAsync(_jobId, s => s.Step = JobSteps.ResearchFile, cancellationToken);
            _owner._logger.LogInformation("Step {Step} (workflow)", JobSteps.ResearchFile);

            if (string.IsNullOrWhiteSpace(_owner._resources.VectorStoreId))
            {
                return state with { File = EmptyFileResearch() };
            }

            var file = await _owner.RunFileResearchSafeAsync(state.EffectivePrompt, state.Planner, cancellationToken);
            await _owner.AddFileSourcesAsync(_jobId, file, cancellationToken);

            return state with { File = file };
        }
    }

    private sealed class GenerateHtmlWorkflowExecutor : Executor<SlideWorkflowState, SlideWorkflowState>
    {
        private readonly SlideGenerationOrchestrator _owner;
        private readonly string _jobId;

        public GenerateHtmlWorkflowExecutor(SlideGenerationOrchestrator owner, string jobId)
            : base(id: "generate_html", options: ExecutorOptions.Default, declareCrossRunShareable: false)
        {
            _owner = owner;
            _jobId = jobId;
        }

        public override async ValueTask<SlideWorkflowState> HandleAsync(
            SlideWorkflowState state,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            await _owner._jobs.UpdateAsync(_jobId, s => s.Step = JobSteps.GenerateHtml, cancellationToken);
            _owner._logger.LogInformation("Step {Step} (workflow attempt {Attempt})", JobSteps.GenerateHtml, state.Attempt + 1);

            var html = await _owner.RunHtmlGeneratorAsync(
                state.EffectivePrompt,
                state.Aspect,
                state.ImageDataUrl,
                state.Planner,
                state.Web,
                state.File,
                state.FixedAppendix,
                cancellationToken);
            html = FoundryResponseParser.StripCodeFences(html).Trim();

            await _owner._jobs.SaveHtmlAsync(_jobId, html, cancellationToken);

            return state with { Html = html };
        }
    }

    private sealed class ValidateWorkflowExecutor : Executor<SlideWorkflowState, SlideWorkflowState>
    {
        private readonly SlideGenerationOrchestrator _owner;
        private readonly string _jobId;

        public ValidateWorkflowExecutor(SlideGenerationOrchestrator owner, string jobId)
            : base(id: "validate", options: ExecutorOptions.Default, declareCrossRunShareable: false)
        {
            _owner = owner;
            _jobId = jobId;
        }

        public override async ValueTask<SlideWorkflowState> HandleAsync(
            SlideWorkflowState state,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            await _owner._jobs.UpdateAsync(_jobId, s => s.Step = JobSteps.Validate, cancellationToken);
            _owner._logger.LogInformation("Step {Step} (workflow attempt {Attempt})", JobSteps.Validate, state.Attempt + 1);

            if (string.IsNullOrWhiteSpace(state.Html))
            {
                throw new InvalidOperationException("Workflow validation failed: missing HTML.");
            }

            var constraints = AspectPrompt.ValidatorConstraintsFor(state.Aspect);
            var validator = await _owner.RunValidatorAsync(state.Html, constraints, cancellationToken);

            var slideCount = CountSlides(state.Html);
            var slideCountIssue = slideCount == 1 ? null : $"Expected exactly 1 <section class=\"slide\"> but found {slideCount}.";

            if (validator.Ok && slideCountIssue is null)
            {
                return state with { Done = true };
            }

            if (state.Attempt >= 2)
            {
                var issues = validator.Issues.Take(8).ToList();
                if (slideCountIssue is not null)
                {
                    issues.Insert(0, slideCountIssue);
                }

                throw new InvalidOperationException("HTML validation failed: " + string.Join(" | ", issues));
            }

            var issuesText = string.Join("\n", validator.Issues.Select(i => $"- {i}"));
            var combinedIssues = slideCountIssue is not null
                ? string.Join("\n", new[] { $"- {slideCountIssue}", issuesText }.Where(s => !string.IsNullOrWhiteSpace(s)))
                : issuesText;

            var fixedAppendix = !string.IsNullOrWhiteSpace(validator.FixedPromptAppendix)
                ? (slideCountIssue is not null ? validator.FixedPromptAppendix + "\n" + slideCountIssue : validator.FixedPromptAppendix)
                : "Fix these issues:\n" + combinedIssues;

            return state with
            {
                FixedAppendix = fixedAppendix,
                Attempt = state.Attempt + 1,
                Done = false
            };
        }
    }

    private sealed class ResultWorkflowExecutor : Executor<SlideWorkflowState, string>
    {
        public ResultWorkflowExecutor()
            : base(id: "result", options: ExecutorOptions.Default, declareCrossRunShareable: false)
        {
        }

        public override async ValueTask<string> HandleAsync(
            SlideWorkflowState state,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(state.Html))
            {
                throw new InvalidOperationException("Workflow result missing HTML.");
            }

            await context.YieldOutputAsync(state.Html, cancellationToken);
            return state.Html;
        }
    }

    private static PlannerOutput BuildFallbackPlannerOutput(string prompt)
    {
        var title = (prompt ?? "").Split('\n', 2, StringSplitOptions.TrimEntries)[0];
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Slide";
        }

        return new PlannerOutput
        {
            SlideCount = 1,
            SlideOutline =
            [
                new SlideOutlineItem
                {
                    Title = title.Length > 80 ? title[..80] : title,
                    Bullets =
                    [
                        "Overview",
                        "Key points",
                        "Summary"
                    ]
                }
            ],
            SearchQueries = [],
            KeyConstraints = []
        };
    }

    private static PlannerOutput NormalizePlannerOutput(PlannerOutput planner, string prompt)
    {
        if (planner.SlideOutline is null || planner.SlideOutline.Count == 0)
        {
            return BuildFallbackPlannerOutput(prompt);
        }

        var slide = planner.SlideOutline[0];
        var title = string.IsNullOrWhiteSpace(slide.Title)
            ? BuildFallbackPlannerOutput(prompt).SlideOutline[0].Title
            : slide.Title;

        var bullets = (slide.Bullets ?? [])
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim())
            .ToList();

        if (bullets.Count < 3)
        {
            bullets.AddRange(new[] { "Overview", "Key points", "Summary" }.Skip(bullets.Count));
        }

        if (bullets.Count > 6)
        {
            bullets = bullets.Take(6).ToList();
        }

        var queries = (planner.SearchQueries ?? [])
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var keyConstraints = (planner.KeyConstraints ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();

        return new PlannerOutput
        {
            SlideCount = 1,
            SlideOutline =
            [
                new SlideOutlineItem
                {
                    Title = title.Length > 80 ? title[..80] : title,
                    Bullets = bullets
                }
            ],
            SearchQueries = queries,
            KeyConstraints = keyConstraints
        };
    }

    private static WebResearchOutput EmptyWebResearch() => new()
    {
        Findings = [],
        Citations = [],
        UsedQueries = []
    };

    private static FileResearchOutput EmptyFileResearch() => new()
    {
        Snippets = [],
        FileCitations = []
    };

    private async Task<WebResearchOutput> RunWebResearchSafeAsync(IReadOnlyList<string> queries, CancellationToken cancellationToken)
    {
        if (queries.Count == 0)
        {
            return EmptyWebResearch();
        }

        try
        {
            return await RunWebResearchAsync(queries, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web research failed. Continuing without web findings.");
            return EmptyWebResearch();
        }
    }

    private async Task<FileResearchOutput> RunFileResearchSafeAsync(string effectivePrompt, PlannerOutput planner, CancellationToken cancellationToken)
    {
        try
        {
            return await RunFileResearchAsync(effectivePrompt, planner, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "File research failed. Continuing without file findings.");
            return EmptyFileResearch();
        }
    }

    private async Task<PlannerOutput> RunPlannerAsync(string effectivePrompt, string? imageDataUrl, CancellationToken cancellationToken)
    {
        var body = FoundryRequestBuilder.BuildJsonSchemaResponseRequest(
            model: _options.ModelDeploymentName,
            instructions: Instructions.Planner,
            input: FoundryRequestBuilder.BuildUserInput(effectivePrompt, imageDataUrl),
            tools: [],
            schemaName: "planner",
            schema: JsonSchemas.PlannerSchema);

        using var response = await _foundry.CreateResponseAsync(body, cancellationToken);
        return FoundryResponseParser.ParseJsonFromOutputText<PlannerOutput>(response);
    }

    private async Task<WebResearchOutput> RunWebResearchAsync(IReadOnlyList<string> queries, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        text.AppendLine("Search queries:");
        foreach (var q in queries.Distinct())
        {
            text.AppendLine($"- {q}");
        }

        var body = FoundryRequestBuilder.BuildJsonSchemaResponseRequest(
            model: _options.ModelDeploymentName,
            instructions: Instructions.WebResearch,
            input: FoundryRequestBuilder.BuildUserInput(text.ToString(), imageDataUrl: null),
            tools: [new { type = "web_search_preview" }],
            schemaName: "web_research",
            schema: JsonSchemas.WebResearchSchema);

        using var response = await _foundry.CreateResponseAsync(body, cancellationToken);
        return FoundryResponseParser.ParseJsonFromOutputText<WebResearchOutput>(response);
    }

    private async Task<FileResearchOutput> RunFileResearchAsync(string effectivePrompt, PlannerOutput planner, CancellationToken cancellationToken)
    {
        // file_search relies on a vector store created from /seed-data at startup.
        var vectorStoreId = _resources.VectorStoreId;
        if (string.IsNullOrWhiteSpace(vectorStoreId))
        {
            _logger.LogWarning("Vector store not available. Returning empty file research.");
            return new FileResearchOutput { Snippets = [], FileCitations = [] };
        }

        var keywords = planner.KeyConstraints
            .Concat(planner.SlideOutline.Select(s => s.Title))
            .Distinct()
            .Take(12);

        var text = new StringBuilder();
        text.AppendLine("User prompt (with server-side constraints):");
        text.AppendLine(effectivePrompt);
        text.AppendLine();
        text.AppendLine("Related keywords:");
        foreach (var k in keywords)
        {
            text.AppendLine($"- {k}");
        }

        var body = FoundryRequestBuilder.BuildJsonSchemaResponseRequest(
            model: _options.ModelDeploymentName,
            instructions: Instructions.FileResearch,
            input: FoundryRequestBuilder.BuildUserInput(text.ToString(), imageDataUrl: null),
            tools: [new { type = "file_search", vector_store_ids = new[] { vectorStoreId } }],
            schemaName: "file_research",
            schema: JsonSchemas.FileResearchSchema);

        using var response = await _foundry.CreateResponseAsync(body, cancellationToken);
        return FoundryResponseParser.ParseJsonFromOutputText<FileResearchOutput>(response);
    }

    private async Task<string> GenerateWithValidationLoopAsync(
        string jobId,
        string effectivePrompt,
        string aspect,
        string? imageDataUrl,
        PlannerOutput planner,
        WebResearchOutput web,
        FileResearchOutput file,
        string constraints,
        CancellationToken cancellationToken)
    {
        string? fixedAppendix = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await _jobs.UpdateAsync(jobId, s => s.Step = JobSteps.GenerateHtml, cancellationToken);
            _logger.LogInformation("Step {Step} (attempt {Attempt})", JobSteps.GenerateHtml, attempt + 1);
            var html = await RunHtmlGeneratorAsync(effectivePrompt, aspect, imageDataUrl, planner, web, file, fixedAppendix, cancellationToken);
            html = FoundryResponseParser.StripCodeFences(html).Trim();

            await _jobs.SaveHtmlAsync(jobId, html, cancellationToken);

            await _jobs.UpdateAsync(jobId, s => s.Step = JobSteps.Validate, cancellationToken);
            _logger.LogInformation("Step {Step} (attempt {Attempt})", JobSteps.Validate, attempt + 1);
            var validator = await RunValidatorAsync(html, constraints, cancellationToken);

            var slideCount = CountSlides(html);
            var slideCountIssue = slideCount == 1 ? null : $"Expected exactly 1 <section class=\"slide\"> but found {slideCount}.";

            if (validator.Ok && slideCountIssue is null)
            {
                return html;
            }

            if (attempt >= 2)
            {
                var issues = validator.Issues.Take(8).ToList();
                if (slideCountIssue is not null)
                {
                    issues.Insert(0, slideCountIssue);
                }

                throw new InvalidOperationException("HTML validation failed: " + string.Join(" | ", issues));
            }

            var issuesText = string.Join("\n", validator.Issues.Select(i => $"- {i}"));
            var combinedIssues = slideCountIssue is not null
                ? string.Join("\n", new[] { $"- {slideCountIssue}", issuesText }.Where(s => !string.IsNullOrWhiteSpace(s)))
                : issuesText;

            fixedAppendix = !string.IsNullOrWhiteSpace(validator.FixedPromptAppendix)
                ? (slideCountIssue is not null ? validator.FixedPromptAppendix + "\n" + slideCountIssue : validator.FixedPromptAppendix)
                : "Fix these issues:\n" + combinedIssues;
        }

        throw new InvalidOperationException("GenerateWithValidationLoopAsync: unreachable.");
    }

    private static int CountSlides(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return 0;
        }

        return Regex.Matches(
            html,
            "<section\\b[^>]*class\\s*=\\s*(['\"])\\s*[^'\"]*\\bslide\\b[^'\"]*\\1[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
    }

    private async Task<string> RunHtmlGeneratorAsync(
        string effectivePrompt,
        string aspect,
        string? imageDataUrl,
        PlannerOutput planner,
        WebResearchOutput web,
        FileResearchOutput file,
        string? fixedAppendix,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        text.AppendLine("USER PROMPT:");
        text.AppendLine(effectivePrompt);
        text.AppendLine();

        text.AppendLine("SLIDE OUTLINE (Plan):");
        for (var i = 0; i < planner.SlideOutline.Count; i++)
        {
            var slide = planner.SlideOutline[i];
            text.AppendLine($"Slide {i + 1}: {slide.Title}");
            foreach (var b in slide.Bullets)
            {
                text.AppendLine($"- {b}");
            }
        }

        if (web.Findings.Count > 0 || web.Citations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("WEB RESEARCH:");
            foreach (var f in web.Findings.Take(24))
            {
                text.AppendLine($"- {f}");
            }

            if (web.Citations.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("CITATIONS (URLs):");
                foreach (var c in web.Citations.Take(16))
                {
                    text.AppendLine($"- {c.Title}: {c.Url}");
                }
            }
        }

        if (file.Snippets.Count > 0 || file.FileCitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("FILE RESEARCH:");
            foreach (var s in file.Snippets.Take(24))
            {
                text.AppendLine($"- {s}");
            }

            if (file.FileCitations.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("FILE CITATIONS:");
                foreach (var c in file.FileCitations.Take(16))
                {
                    text.AppendLine($"- {c.Filename} ({c.FileId}): {c.Snippet}");
                }
            }
        }

        text.AppendLine();
        text.AppendLine("ASPECT TEMPLATE CONSTRAINTS:");
        text.AppendLine(AspectPrompt.TemplateConstraintsForAspect(planner, aspect));

        if (!string.IsNullOrWhiteSpace(fixedAppendix))
        {
            text.AppendLine();
            text.AppendLine("FIX APPENDIX (from validator):");
            text.AppendLine(fixedAppendix);
        }

        var body = FoundryRequestBuilder.BuildTextResponseRequest(
            model: _options.ModelDeploymentName,
            instructions: Instructions.HtmlGenerator,
            input: FoundryRequestBuilder.BuildUserInput(text.ToString(), imageDataUrl),
            tools: []);

        using var response = await _foundry.CreateResponseAsync(body, cancellationToken);
        return FoundryResponseParser.ExtractOutputText(response);
    }

    private async Task<ValidatorOutput> RunValidatorAsync(string html, string constraints, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        text.AppendLine("CONSTRAINTS:");
        text.AppendLine(constraints);
        text.AppendLine();
        text.AppendLine("HTML:");
        text.AppendLine(html);

        var body = FoundryRequestBuilder.BuildJsonSchemaResponseRequest(
            model: _options.ModelDeploymentName,
            instructions: Instructions.Validator,
            input: FoundryRequestBuilder.BuildUserInput(text.ToString(), imageDataUrl: null),
            tools: [],
            schemaName: "validator",
            schema: JsonSchemas.ValidatorSchema);

        using var response = await _foundry.CreateResponseAsync(body, cancellationToken);
        return FoundryResponseParser.ParseJsonFromOutputText<ValidatorOutput>(response);
    }

    private Task AddWebSourcesAsync(string jobId, WebResearchOutput web, CancellationToken cancellationToken)
    {
        var urls = web.Citations
            .Select(c => c.Url)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return _jobs.UpdateAsync(jobId, state =>
        {
            foreach (var url in urls)
            {
                state.Sources.Urls.Add(url);
            }
        }, cancellationToken);
    }

    private Task AddFileSourcesAsync(string jobId, FileResearchOutput file, CancellationToken cancellationToken)
    {
        var files = file.FileCitations
            .Select(c => c.Filename)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return _jobs.UpdateAsync(jobId, state =>
        {
            foreach (var f in files)
            {
                state.Sources.Files.Add(f);
            }
        }, cancellationToken);
    }
}

public static class AspectPrompt
{
    public static (int Width, int Height, int SafeMargin) GetCanvas(string aspect) => aspect switch
    {
        "4:3" => (1024, 768, 48),
        _ => (1920, 1080, 64)
    };

    public static string AppendixFor(string aspect) => aspect switch
    {
        "4:3" => """
                 出力HTMLは4:3。基準キャンバスは 1024x768 px。全スライドは同一サイズ。
                 スライド外周のセーフマージンは 48px。要素はキャンバス内に収め、はみ出し禁止。
                 """,
        _ => """
             出力HTMLは16:9。基準キャンバスは 1920x1080 px。全スライドは同一サイズ。
             スライド外周のセーフマージンは 64px。要素はキャンバス内に収め、はみ出し禁止。
             """
    };

    public static string ComposeEffectivePrompt(string rawPrompt, string aspect)
        => $"{rawPrompt}\n\n---\n{AppendixFor(aspect)}";

    public static string ValidatorConstraintsFor(string aspect) => aspect switch
    {
        "4:3" => """
                 - Canvas size: 1024x768 px
                 - Each slide: <section class="slide"> with width:1024px;height:768px
                 - Slide count: 1 (exactly one <section class="slide">)
                 - No <script> tags
                 - No external http/https resources in href/src
                 - System fonts only
                 """,
        _ => """
             - Canvas size: 1920x1080 px
             - Each slide: <section class="slide"> with width:1920px;height:1080px
             - Slide count: 1 (exactly one <section class="slide">)
             - No <script> tags
             - No external http/https resources in href/src
             - System fonts only
             """
    };

    public static string TemplateConstraintsForAspect(PlannerOutput planner, string aspect)
    {
        var (w, h, safe) = GetCanvas(aspect);
        return
            $"""
             - HTML is a single file
             - One slide = one <section class="slide">
             - Canvas size: {w}x{h}px. Set .slide width:{w}px; height:{h}px; box-sizing:border-box;
             - Safe margin: {safe}px (keep text inside)
             - Use system fonts only (no @import, no <link> to fonts)
             - No <script> tags
             - No external assets (no http/https in src/href). Use pure CSS shapes/gradients instead of images.
             - Render as vertical stacked slides (for PNG screenshot preview).
             - Slide count: 1
             """;
    }
}
