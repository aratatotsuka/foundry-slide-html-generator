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
        _logger.LogInformation("Foundry provisioning started.");

        try
        {
            // File research (vector store + file_search) is temporarily disabled.
            _resources.VectorStoreId = null;
            if (_options.UseConnectedAgents)
            {
                try
                {
                    await EnsureAssistantsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Assistant provisioning failed. Connected Agents mode will be disabled for this process.");
                    _resources.AssistantIds.Clear();
                }
            }

            try
            {
                await EnsureAgentsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prompt-agent provisioning failed. Continuing without provisioned agents.");
                _resources.AgentIds.Clear();
            }

            if (_options.UseFoundryWorkflow)
            {
                try
                {
                    await EnsureWorkflowAgentAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Workflow agent lookup failed. Foundry workflow mode will be unavailable for this process.");
                    _resources.AgentIds.Remove(_options.FoundryWorkflowName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Foundry provisioning failed.");
        }
        finally
        {
            _resources.MarkReady();
            _logger.LogInformation("Foundry provisioning completed.");
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
            AgentDefinitions.WebResearch(),
            AgentDefinitions.HtmlGenerator(),
            AgentDefinitions.Validator()
        };

        if (!string.IsNullOrWhiteSpace(_resources.VectorStoreId))
        {
            definitions.Add(AgentDefinitions.FileResearch(_resources.VectorStoreId));
        }

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

    private async Task EnsureAssistantsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> existing;
        try
        {
            existing = await _client.ListAssistantsByNameAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list existing assistants. Will attempt create blindly.");
            existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Create/update leaf assistants first (these are invoked via connected_agent tools).
        var leafDefinitions = new List<AssistantDefinition>
        {
            new(AssistantNames.HtmlGenerator, Instructions.HtmlGenerator, Tools: []),
            new(AssistantNames.Validator, Instructions.Validator, Tools: [])
        };

        foreach (var definition in leafDefinitions)
        {
            if (existing.TryGetValue(definition.Name, out var id))
            {
                _logger.LogInformation("Updating assistant {Name} ({Id})", definition.Name, id);
                await _client.UpdateAssistantAsync(id, definition, cancellationToken);
                _resources.AssistantIds[definition.Name] = id;
            }
            else
            {
                _logger.LogInformation("Creating assistant {Name}", definition.Name);
                var createdId = await _client.CreateAssistantAsync(definition, cancellationToken);
                _resources.AssistantIds[definition.Name] = createdId;
                existing = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase)
                {
                    [definition.Name] = createdId
                };
            }
        }

        if (!_resources.AssistantIds.TryGetValue(AssistantNames.HtmlGenerator, out var htmlGeneratorId)
            || !_resources.AssistantIds.TryGetValue(AssistantNames.Validator, out var validatorId))
        {
            throw new InvalidOperationException("Assistant provisioning failed: missing leaf assistant ids.");
        }

        var orchestratorDefinition = new AssistantDefinition(
            AssistantNames.Planner,
            Instructions.ConnectedOrchestrator,
            Tools:
            [
                new
                {
                    type = "connected_agent",
                    connected_agent = new
                    {
                        id = htmlGeneratorId,
                        name = "html_generator",
                        description = "Generate the final self-contained HTML for a single slide."
                    }
                },
                new
                {
                    type = "connected_agent",
                    connected_agent = new
                    {
                        id = validatorId,
                        name = "validator",
                        description = "Validate generated HTML and return JSON with issues and fix appendix."
                    }
                }
            ]);

        if (existing.TryGetValue(orchestratorDefinition.Name, out var existingOrchestratorId))
        {
            _logger.LogInformation("Updating assistant {Name} ({Id})", orchestratorDefinition.Name, existingOrchestratorId);
            await _client.UpdateAssistantAsync(existingOrchestratorId, orchestratorDefinition, cancellationToken);
            _resources.AssistantIds[orchestratorDefinition.Name] = existingOrchestratorId;
        }
        else
        {
            _logger.LogInformation("Creating assistant {Name}", orchestratorDefinition.Name);
            var createdId = await _client.CreateAssistantAsync(orchestratorDefinition, cancellationToken);
            _resources.AssistantIds[orchestratorDefinition.Name] = createdId;
        }
    }

    private async Task EnsureWorkflowAgentAsync(CancellationToken cancellationToken)
    {
        var workflowName = _options.FoundryWorkflowName?.Trim();
        if (string.IsNullOrWhiteSpace(workflowName))
        {
            _logger.LogWarning("USE_FOUNDRY_WORKFLOW=true but FOUNDRY_WORKFLOW_NAME is empty. Foundry workflow mode will be unavailable.");
            return;
        }

        IReadOnlyDictionary<string, string> existingAgents;
        try
        {
            existingAgents = await _client.ListAgentsByNameAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list agents while looking up workflow '{WorkflowName}'.", workflowName);
            return;
        }

        if (existingAgents.TryGetValue(workflowName, out var id) && !string.IsNullOrWhiteSpace(id))
        {
            _logger.LogInformation("Using Foundry workflow agent {Name} ({Id})", workflowName, id);
            _resources.AgentIds[workflowName] = id;
            return;
        }

        _logger.LogWarning(
            "Foundry workflow agent '{WorkflowName}' was not found. Create it in Foundry portal (Workflows) before enabling USE_FOUNDRY_WORKFLOW.",
            workflowName);
    }
}
