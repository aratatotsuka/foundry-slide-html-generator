using Microsoft.Extensions.Options;
using FoundrySlideHtmlGenerator.Backend.State;

namespace FoundrySlideHtmlGenerator.Backend.Foundry;

public sealed class FoundryProvisioningService : BackgroundService
{
    private readonly IFoundryClient _client;
    private readonly FoundryResourceContext _resources;
    private readonly IStateStore _stateStore;
    private readonly FoundryOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<FoundryProvisioningService> _logger;

    public FoundryProvisioningService(
        IFoundryClient client,
        FoundryResourceContext resources,
        IStateStore stateStore,
        IOptions<FoundryOptions> options,
        IWebHostEnvironment environment,
        ILogger<FoundryProvisioningService> logger)
    {
        _client = client;
        _resources = resources;
        _stateStore = stateStore;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Foundry provisioning started.");

            // File research (vector store + file_search) is temporarily disabled.
            _resources.VectorStoreId = null;
            await EnsureAgentsAsync(stoppingToken);

            _resources.MarkReady();
            _logger.LogInformation("Foundry provisioning completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Foundry provisioning failed.");
            _resources.MarkFailed(ex);
        }
    }

    private async Task<string?> EnsureVectorStoreAsync(CancellationToken cancellationToken)
    {
        var existing = await _stateStore.GetAsync("vectorStoreId", cancellationToken);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            _logger.LogInformation("Using existing vector store id from state store: {VectorStoreId}", existing);
            await _client.WaitForVectorStoreReadyAsync(existing, timeout: TimeSpan.FromMinutes(1), cancellationToken);
            return existing;
        }

        var seedDir = ResolveSeedDataDirectory();
        if (seedDir is null)
        {
            _logger.LogWarning("seed-data directory not found; file_search will be unavailable.");
            return null;
        }

        var files = Directory
            .EnumerateFiles(seedDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length == 0)
        {
            _logger.LogWarning("seed-data directory is empty; file_search will be unavailable.");
            return null;
        }

        _logger.LogInformation("Uploading {Count} seed files to Foundry...", files.Length);
        var fileIds = new List<string>();
        foreach (var file in files)
        {
            var fileId = await _client.UploadFileAsync(file, cancellationToken);
            fileIds.Add(fileId);
            _logger.LogInformation("Uploaded seed file {File} -> {FileId}", Path.GetFileName(file), fileId);
        }

        var vectorStoreId = await _client.CreateVectorStoreAsync("seed-data", fileIds, cancellationToken);
        _logger.LogInformation("Created vector store {VectorStoreId}", vectorStoreId);

        await _client.WaitForVectorStoreReadyAsync(vectorStoreId, timeout: TimeSpan.FromMinutes(2), cancellationToken);
        await _stateStore.SetAsync("vectorStoreId", vectorStoreId, cancellationToken);

        return vectorStoreId;
    }

    private async Task EnsureAgentsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> existing;
        try
        {
            existing = await _client.ListAgentsByNameAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list existing agents. Will attempt create blindly.");
            existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var definitions = new List<AgentDefinition>
        {
            AgentDefinitions.Planner(),
            AgentDefinitions.HtmlGenerator(),
            AgentDefinitions.Validator()
        };

        // Note: File research agent is intentionally not provisioned while the feature is disabled.

        foreach (var definition in definitions)
        {
            if (existing.TryGetValue(definition.Name, out var id))
            {
                _logger.LogInformation("Updating agent {Name} ({Id})", definition.Name, id);
                await _client.UpdateAgentAsync(id, definition, cancellationToken);
                _resources.AgentIds[definition.Name] = id;
            }
            else
            {
                _logger.LogInformation("Creating agent {Name}", definition.Name);
                var createdId = await _client.CreateAgentAsync(definition, cancellationToken);
                _resources.AgentIds[definition.Name] = createdId;
            }
        }
    }

    private string? ResolveSeedDataDirectory()
    {
        // Prefer configured value (relative to content root).
        var configured = _options.SeedDataDir;
        var direct = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configured));

        if (Directory.Exists(direct))
        {
            return direct;
        }

        // Fallback: walk up and find "seed-data" directory.
        var current = new DirectoryInfo(_environment.ContentRootPath);
        for (var i = 0; i < 6 && current is not null; i++)
        {
            var candidate = Path.Combine(current.FullName, "seed-data");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}
