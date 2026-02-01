using Azure.Core;
using Azure.Identity;
using FoundrySlideHtmlGenerator.Backend.Contracts;
using FoundrySlideHtmlGenerator.Backend.Foundry;
using FoundrySlideHtmlGenerator.Backend.Jobs;
using FoundrySlideHtmlGenerator.Backend.Orchestration;
using FoundrySlideHtmlGenerator.Backend.Rendering;
using FoundrySlideHtmlGenerator.Backend.State;
using FoundrySlideHtmlGenerator.Backend.Utilities;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

var corsAllowedOrigins =
    (builder.Configuration["CORS_ALLOWED_ORIGINS"] ?? "http://localhost:5173")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(corsAllowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// Guard against accidental huge base64 payloads (imageBase64)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 12 * 1024 * 1024; // 12MB
});

builder.Services.AddSingleton<TokenCredential, DefaultAzureCredential>();
builder.Services.AddSingleton<IStateStore>(sp => StateStoreFactory.Create(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<TokenCredential>(), sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<FoundryResourceContext>();

builder.Services.AddOptions<FoundryOptions>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<JobStorageOptions>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<HtmlDownloadOptions>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient<FoundryClient>();
builder.Services.AddSingleton<IFoundryClient>(sp => sp.GetRequiredService<FoundryClient>());

builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<IJobStore, FileJobStore>();
builder.Services.AddSingleton<SlideGenerationOrchestrator>();
builder.Services.AddSingleton<IPngRenderer, PlaywrightPngRenderer>();

builder.Services.AddHostedService<FoundryProvisioningService>();
builder.Services.AddHostedService<JobWorker>();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseCors();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/generate", async (
        GenerateRequest request,
        JobQueue queue,
        IJobStore store,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
    {
        var logger = loggerFactory.CreateLogger("GenerateEndpoint");

        var validation = GenerateRequestValidator.Validate(request);
        if (!validation.Ok)
        {
            return Results.BadRequest(new { error = validation.Error });
        }

        string? imageDataUrl = null;
        if (!string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            var parsed = Base64Image.TryParse(request.ImageBase64!, maxBytes: 4 * 1024 * 1024);
            if (!parsed.Ok)
            {
                return Results.BadRequest(new { error = parsed.Error });
            }

            imageDataUrl = parsed.DataUrl;
        }

        var jobId = Guid.NewGuid().ToString("N");
        await store.CreateAsync(jobId, request, imageDataUrl, cancellationToken);
        queue.Enqueue(new JobWorkItem(jobId));

        logger.LogInformation("Enqueued job {JobId}", jobId);
        return Results.Ok(new GenerateResponse { JobId = jobId });
    })
    .WithName("Generate");

app.MapGet("/api/jobs/{jobId}", async (string jobId, IJobStore store, HttpContext http, CancellationToken cancellationToken) =>
{
    var job = await store.GetAsync(jobId, cancellationToken);
    if (job is null)
    {
        return Results.NotFound();
    }

    var previewUrl = job.Status == JobStatus.Succeeded && job.PreviewPngPath is not null
        ? $"/api/jobs/{jobId}/preview.png"
        : null;

    return Results.Ok(new JobStatusResponse
    {
        Status = job.Status.ToWire(),
        Step = job.Step,
        Error = job.Error,
        PreviewPngUrl = previewUrl,
        Sources = new JobSources
        {
            Urls = job.Sources.Urls.ToArray(),
            Files = job.Sources.Files.ToArray()
        }
    });
});

app.MapGet("/api/jobs/{jobId}/preview.png", async (string jobId, IJobStore store, CancellationToken cancellationToken) =>
{
    var job = await store.GetAsync(jobId, cancellationToken);
    if (job is null)
    {
        return Results.NotFound();
    }

    string? pngPath = null;
    if (job.PreviewPngPath is not null)
    {
        try
        {
            pngPath = Path.GetFullPath(job.PreviewPngPath);
        }
        catch
        {
            return Results.NotFound();
        }
    }
    if (job.Status != JobStatus.Succeeded || pngPath is null || !File.Exists(pngPath))
    {
        return Results.NotFound();
    }

    return Results.File(pngPath, "image/png");
});

app.MapGet("/api/jobs/{jobId}/result.html", async (
        string jobId,
        IJobStore store,
        IOptions<HtmlDownloadOptions> downloadOptions,
        HttpRequest httpRequest,
        CancellationToken cancellationToken) =>
    {
        var opts = downloadOptions.Value;
        if (!opts.AllowHtmlDownload)
        {
            return Results.NotFound();
        }

        if (!string.IsNullOrWhiteSpace(opts.DownloadApiKey))
        {
            if (!httpRequest.Headers.TryGetValue("X-Download-Key", out var provided) || provided != opts.DownloadApiKey)
            {
                return Results.Unauthorized();
            }
        }

        var job = await store.GetAsync(jobId, cancellationToken);
        if (job is null)
        {
            return Results.NotFound();
        }

        if (job.Status != JobStatus.Succeeded || job.ResultHtmlPath is null)
        {
            return Results.NotFound();
        }

        string htmlPath;
        try
        {
            htmlPath = Path.GetFullPath(job.ResultHtmlPath);
        }
        catch
        {
            return Results.NotFound();
        }

        if (!File.Exists(htmlPath))
        {
            return Results.NotFound();
        }

        return Results.File(htmlPath, "text/html; charset=utf-8", fileDownloadName: $"{jobId}.html");
    });

app.Run();
