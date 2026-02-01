using FoundrySlideHtmlGenerator.Backend.Contracts;

namespace FoundrySlideHtmlGenerator.Backend.Jobs;

public interface IJobStore
{
    Task CreateAsync(string jobId, GenerateRequest request, string? imageDataUrl, CancellationToken cancellationToken);
    Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken);
    Task<JobInput?> GetInputAsync(string jobId, CancellationToken cancellationToken);
    Task UpdateAsync(string jobId, Action<JobState> mutate, CancellationToken cancellationToken);
    Task SaveHtmlAsync(string jobId, string html, CancellationToken cancellationToken);
    Task SavePreviewPngAsync(string jobId, byte[] pngBytes, CancellationToken cancellationToken);
}

public sealed class JobInput
{
    public required string Prompt { get; init; }
    public required string Aspect { get; init; }
    public string? ImageDataUrl { get; init; }
}

