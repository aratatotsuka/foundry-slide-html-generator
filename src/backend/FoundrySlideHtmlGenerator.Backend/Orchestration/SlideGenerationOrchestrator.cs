using System.Text;
using System.Text.RegularExpressions;
using FoundrySlideHtmlGenerator.Backend.Foundry;
using FoundrySlideHtmlGenerator.Backend.Jobs;
using FoundrySlideHtmlGenerator.Backend.Rendering;
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

        // 1) Planner (single-slide mode: local plan)
        var title = (input.Prompt ?? "").Split('\n', 2, StringSplitOptions.TrimEntries)[0];
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Slide";
        }

        var planner = new PlannerOutput
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

        // 2) Web research
        WebResearchOutput webResearch;
        if (planner.SearchQueries.Count > 0)
        {
            await _jobs.UpdateAsync(workItem.JobId, s => s.Step = JobSteps.ResearchWeb, cancellationToken);
            _logger.LogInformation("Step {Step}", JobSteps.ResearchWeb);
            webResearch = await RunWebResearchAsync(planner.SearchQueries, cancellationToken);
            await AddWebSourcesAsync(workItem.JobId, webResearch, cancellationToken);
        }
        else
        {
            webResearch = new WebResearchOutput { Findings = [], Citations = [], UsedQueries = [] };
        }

        // 3) File research
        await _jobs.UpdateAsync(workItem.JobId, s => s.Step = JobSteps.ResearchFile, cancellationToken);
        _logger.LogInformation("Step {Step}", JobSteps.ResearchFile);
        var fileResearch = await RunFileResearchAsync(effectivePrompt, planner, cancellationToken);
        await AddFileSourcesAsync(workItem.JobId, fileResearch, cancellationToken);

        // 4/5) Generate + validate loop
        var constraints = AspectPrompt.ValidatorConstraintsFor(input.Aspect);
        var html = await GenerateWithValidationLoopAsync(
            workItem.JobId,
            effectivePrompt,
            input.Aspect,
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
            var html = await RunHtmlGeneratorAsync(effectivePrompt, aspect, planner, web, file, fixedAppendix, cancellationToken);
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
            input: FoundryRequestBuilder.BuildUserInput(text.ToString(), imageDataUrl: null),
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
