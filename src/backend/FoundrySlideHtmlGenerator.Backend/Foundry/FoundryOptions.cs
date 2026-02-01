using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace FoundrySlideHtmlGenerator.Backend.Foundry;

public sealed class FoundryOptions
{
    [ConfigurationKeyName("FOUNDRY_PROJECT_ENDPOINT")]
    [Required]
    public string ProjectEndpoint { get; init; } = "";

    [ConfigurationKeyName("FOUNDRY_API_VERSION")]
    [Required]
    public string ApiVersion { get; init; } = "2025-11-15-preview";

    [ConfigurationKeyName("MODEL_DEPLOYMENT_NAME")]
    [Required]
    public string ModelDeploymentName { get; init; } = "";

    [ConfigurationKeyName("USE_AGENT_APPLICATION")]
    public bool UseAgentApplication { get; init; } = false;

    [ConfigurationKeyName("FOUNDRY_APPLICATION_ENDPOINT")]
    public string? ApplicationEndpoint { get; init; }

    [ConfigurationKeyName("USE_CONNECTED_AGENTS")]
    public bool UseConnectedAgents { get; init; } = false;

    // Runs a Foundry portal workflow (Declarative Workflow Agent) via /openai/responses with an AgentReference.
    // This is the mode that produces Foundry-side Traces/Runs for the workflow resource itself.
    [ConfigurationKeyName("USE_FOUNDRY_WORKFLOW")]
    public bool UseFoundryWorkflow { get; init; } = false;

    // The workflow name as shown in Foundry portal Workflows (agent name).
    [ConfigurationKeyName("FOUNDRY_WORKFLOW_NAME")]
    public string FoundryWorkflowName { get; init; } = "slide-html-generator";

    // Runs the multi-agent flow via Microsoft Agent Framework Workflows (graph orchestration),
    // instead of imperative orchestration code.
    [ConfigurationKeyName("USE_WORKFLOWS")]
    public bool UseWorkflows { get; init; } = true;

    [ConfigurationKeyName("SEED_DATA_DIR")]
    public string SeedDataDir { get; init; } = "seed-data";

    [ConfigurationKeyName("FOUNDRY_HTTP_TIMEOUT_SECONDS")]
    [Range(10, 600)]
    public int HttpTimeoutSeconds { get; init; } = 600;
}
