namespace FoundrySlideHtmlGenerator.Backend.Orchestration;

public sealed class PlannerOutput
{
    public int SlideCount { get; init; }
    public required List<SlideOutlineItem> SlideOutline { get; init; }
    public required List<string> SearchQueries { get; init; }
    public required List<string> KeyConstraints { get; init; }
}

public sealed class SlideOutlineItem
{
    public required string Title { get; init; }
    public required List<string> Bullets { get; init; }
}

public sealed class WebResearchOutput
{
    public required List<string> Findings { get; init; }
    public required List<WebCitation> Citations { get; init; }
    public required List<string> UsedQueries { get; init; }
}

public sealed class WebCitation
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Quote { get; init; }
}

public sealed class FileResearchOutput
{
    public required List<string> Snippets { get; init; }
    public required List<FileCitation> FileCitations { get; init; }
}

public sealed class FileCitation
{
    public required string FileId { get; init; }
    public required string Filename { get; init; }
    public required string Snippet { get; init; }
}

public sealed class ValidatorOutput
{
    public bool Ok { get; init; }
    public required List<string> Issues { get; init; }
    public string? FixedPromptAppendix { get; init; }
}

