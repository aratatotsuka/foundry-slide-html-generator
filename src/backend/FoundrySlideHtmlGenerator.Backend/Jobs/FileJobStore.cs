using System.Collections.Concurrent;
using System.Text.Json;
using FoundrySlideHtmlGenerator.Backend.Contracts;
using Microsoft.Extensions.Options;

namespace FoundrySlideHtmlGenerator.Backend.Jobs;

public sealed class FileJobStore : IJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly JobStorageOptions _options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public FileJobStore(IOptions<JobStorageOptions> options)
    {
        _options = options.Value;
        Directory.CreateDirectory(_options.JobDataDir);
    }

    public async Task CreateAsync(string jobId, GenerateRequest request, string? imageDataUrl, CancellationToken cancellationToken)
    {
        var jobDir = GetJobDir(jobId);
        Directory.CreateDirectory(jobDir);

        var state = new JobState
        {
            JobId = jobId,
            Status = JobStatus.Queued,
            Step = null,
            Error = null,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        string? imagePath = null;
        if (!string.IsNullOrWhiteSpace(imageDataUrl))
        {
            var parsed = ParseDataUrl(imageDataUrl!);
            var extension = parsed.MimeType == "image/png" ? "png" : "jpg";
            imagePath = Path.Combine(jobDir, $"input.{extension}");
            await File.WriteAllBytesAsync(imagePath, parsed.Bytes, cancellationToken);
        }

        var storedRequest = new StoredGenerateRequest
        {
            Prompt = request.Prompt,
            Aspect = request.Aspect,
            ImagePath = imagePath
        };

        await File.WriteAllTextAsync(Path.Combine(jobDir, "request.json"), JsonSerializer.Serialize(storedRequest, JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(jobDir, "state.json"), JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
    }

    public async Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(GetJobDir(jobId), "state.json");
        if (!File.Exists(statePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(statePath, cancellationToken);
        return JsonSerializer.Deserialize<JobState>(json, JsonOptions);
    }

    public async Task<JobInput?> GetInputAsync(string jobId, CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(GetJobDir(jobId), "request.json");
        if (!File.Exists(requestPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(requestPath, cancellationToken);
        var stored = JsonSerializer.Deserialize<StoredGenerateRequest>(json, JsonOptions);
        if (stored is null)
        {
            return null;
        }

        string? imageDataUrl = null;
        if (!string.IsNullOrWhiteSpace(stored.ImagePath) && File.Exists(stored.ImagePath))
        {
            var bytes = await File.ReadAllBytesAsync(stored.ImagePath, cancellationToken);
            var mime = DetectMime(bytes) ?? "image/png";
            imageDataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }

        return new JobInput
        {
            Prompt = stored.Prompt ?? "",
            Aspect = stored.Aspect ?? "16:9",
            ImageDataUrl = imageDataUrl
        };
    }

    public async Task UpdateAsync(string jobId, Action<JobState> mutate, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(jobId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetAsync(jobId, cancellationToken) ?? new JobState { JobId = jobId };
            mutate(state);
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var statePath = Path.Combine(GetJobDir(jobId), "state.json");
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveHtmlAsync(string jobId, string html, CancellationToken cancellationToken)
    {
        var jobDir = GetJobDir(jobId);
        Directory.CreateDirectory(jobDir);
        var htmlPath = Path.Combine(jobDir, "result.html");

        await File.WriteAllTextAsync(htmlPath, html, cancellationToken);
        await UpdateAsync(jobId, state => state.ResultHtmlPath = htmlPath, cancellationToken);
    }

    public async Task SavePreviewPngAsync(string jobId, byte[] pngBytes, CancellationToken cancellationToken)
    {
        var jobDir = GetJobDir(jobId);
        Directory.CreateDirectory(jobDir);
        var pngPath = Path.Combine(jobDir, "preview.png");

        await File.WriteAllBytesAsync(pngPath, pngBytes, cancellationToken);
        await UpdateAsync(jobId, state => state.PreviewPngPath = pngPath, cancellationToken);
    }

    private string GetJobDir(string jobId) => Path.Combine(_options.JobDataDir, jobId);

    private static (string MimeType, byte[] Bytes) ParseDataUrl(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        var header = dataUrl[..comma];
        var payload = dataUrl[(comma + 1)..];
        var mime = header["data:".Length..].Split(';', 2)[0];
        var bytes = Convert.FromBase64String(payload);
        return (mime, bytes);
    }

    private static string? DetectMime(ReadOnlySpan<byte> bytes)
    {
        // PNG
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        // JPEG
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        return null;
    }

    private sealed class StoredGenerateRequest
    {
        public string? Prompt { get; init; }
        public string? Aspect { get; init; }
        public string? ImagePath { get; init; }
    }
}
