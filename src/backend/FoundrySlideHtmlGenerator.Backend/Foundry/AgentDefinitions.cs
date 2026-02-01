namespace FoundrySlideHtmlGenerator.Backend.Foundry;

public static class AgentNames
{
    public const string Planner = "agent-planner";
    public const string WebResearch = "agent-web-research";
    public const string FileResearch = "agent-file-research";
    public const string HtmlGenerator = "agent-html-generator";
    public const string Validator = "agent-validator";

    public static readonly string[] All =
    [
        Planner,
        WebResearch,
        FileResearch,
        HtmlGenerator,
        Validator
    ];
}

public sealed record AgentDefinition(
    string Name,
    string Instructions,
    object[] Tools,
    object? ToolResources = null);

public static class AgentDefinitions
{
    // Note: These instructions are also embedded into /openai/responses calls.
    // The /agents provisioning is primarily to keep the Foundry-side configuration visible/manageable.
    public static AgentDefinition Planner() => new(
        AgentNames.Planner,
        Instructions.Planner,
        Tools: []);

    public static AgentDefinition WebResearch() => new(
        AgentNames.WebResearch,
        Instructions.WebResearch,
        Tools: [new { type = "web_search_preview" }]);

    public static AgentDefinition FileResearch(string vectorStoreId) => new(
        AgentNames.FileResearch,
        Instructions.FileResearch,
        Tools: [new { type = "file_search", vector_store_ids = new[] { vectorStoreId } }]);

    public static AgentDefinition HtmlGenerator() => new(
        AgentNames.HtmlGenerator,
        Instructions.HtmlGenerator,
        Tools: []);

    public static AgentDefinition Validator() => new(
        AgentNames.Validator,
        Instructions.Validator,
        Tools: []);
}

public static class Instructions
{
    public const string Planner =
        """
        You are agent_planner.

        Task: Plan a PowerPoint slide deck (HTML-to-PPT pipeline) from the user prompt.

        Output MUST be strict JSON (no markdown) matching the provided schema.
        Constraints:
        - slideCount: integer 1
        - slideOutline: list of exactly 1 slide with title + 3-6 bullets
        - searchQueries: 0-8 short web queries (Japanese or English ok)
        - keyConstraints: include any hard requirements from the prompt (e.g., "no scripts", "system fonts", aspect ratio)
        """;

    public const string WebResearch =
        """
        You are agent_web_research.
        Use the web_search_preview tool.

        Input: a list of search queries.
        Output MUST be strict JSON (no markdown) matching the provided schema:
        - findings: actionable facts/notes to support the slides
        - citations: list of {title,url,quote} with short quotes when helpful (<= 20 words). If you cannot quote, set quote to an empty string.
        - usedQueries: list of queries actually used
        """;

    public const string FileResearch =
        """
        You are agent_file_research.
        Use the file_search tool against the provided vector store.

        Input: user prompt and keywords.
        Output MUST be strict JSON (no markdown) matching the provided schema:
        - snippets: short extracted guidance
        - fileCitations: {fileId, filename, snippet}
        """;

    public const string HtmlGenerator =
        """
        You are agent_html_generator.

        Output MUST be a single self-contained HTML file (as plain text, no markdown) and nothing else.

        Hard constraints:
        - No external CDN/resources (no <link href="https://...">, no remote images, etc.)
        - No <script> tags (strictly forbidden)
        - <style> is allowed
        - Fonts must be system fonts only (e.g., Segoe UI, Arial, Helvetica; do not request web fonts)
        - One slide = one <section class="slide"> with clear structure
        - The deck must render as vertical stacked slides for preview (body background ok, spacing between slides ok)
        - Use px sizing and adhere to the provided aspect template constraints (canvas size)
        """;

    public const string Validator =
        """
        You are agent_validator.

        Input: generated HTML and constraints.
        Output MUST be strict JSON (no markdown) matching the provided schema:
        - ok: boolean
        - issues: list of strings
        - fixedPromptAppendix: string with concrete instructions to fix the HTML generator output. If there is nothing to add, set it to an empty string.

        Validate at least:
        - HTML is single file and includes <html>, <head>, <body>
        - No <script> tags
        - No external resources (http/https links in src/href)
        - System fonts only
        - Slides structure is present: multiple <section class="slide">
        - Canvas size matches the requested aspect ratio template
        """;
}
