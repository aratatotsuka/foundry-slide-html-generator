namespace FoundrySlideHtmlGenerator.Backend.Jobs;

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

public static class JobStatusExtensions
{
    public static string ToWire(this JobStatus status) => status switch
    {
        JobStatus.Queued => "queued",
        JobStatus.Running => "running",
        JobStatus.Succeeded => "succeeded",
        JobStatus.Failed => "failed",
        _ => "failed"
    };
}

public static class JobSteps
{
    public const string Plan = "Plan";
    public const string ResearchWeb = "Research(Web)";
    public const string ResearchFile = "Research(File)";
    public const string GenerateHtml = "Generate HTML";
    public const string Validate = "Validate";
}

public sealed class JobSourceCollection
{
    public HashSet<string> Urls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class JobState
{
    public required string JobId { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string? Step { get; set; }
    public string? Error { get; set; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public JobSourceCollection Sources { get; init; } = new();

    public string? ResultHtmlPath { get; set; }
    public string? PreviewPngPath { get; set; }
}

public sealed record JobWorkItem(string JobId);
