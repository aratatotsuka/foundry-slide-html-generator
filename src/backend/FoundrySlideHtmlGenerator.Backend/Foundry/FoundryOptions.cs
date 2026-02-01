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

    [ConfigurationKeyName("SEED_DATA_DIR")]
    public string SeedDataDir { get; init; } = "seed-data";

    [ConfigurationKeyName("FOUNDRY_HTTP_TIMEOUT_SECONDS")]
    [Range(10, 600)]
    public int HttpTimeoutSeconds { get; init; } = 600;
}
