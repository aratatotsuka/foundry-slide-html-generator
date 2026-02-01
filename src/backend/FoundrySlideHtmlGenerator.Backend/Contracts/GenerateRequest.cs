namespace FoundrySlideHtmlGenerator.Backend.Contracts;

public sealed class GenerateRequest
{
    public string Prompt { get; init; } = "";

    // "16:9" or "4:3"
    public string Aspect { get; init; } = "16:9";

    // Optional. Can be raw base64 or a data URL (data:image/png;base64,...)
    public string? ImageBase64 { get; init; }
}

public sealed class GenerateResponse
{
    public required string JobId { get; init; }
}

public sealed class JobStatusResponse
{
    public required string Status { get; init; }
    public string? Step { get; init; }
    public string? Error { get; init; }
    public string? PreviewPngUrl { get; init; }
    public JobSources? Sources { get; init; }
}

public sealed class JobSources
{
    public required string[] Urls { get; init; }
    public required string[] Files { get; init; }
}
