using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FoundrySlideHtmlGenerator.Backend.State;

public static class StateStoreFactory
{
    public static IStateStore Create(IConfiguration configuration, TokenCredential credential, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("StateStoreFactory");
        var kind = (configuration["STATE_STORE"] ?? "local").Trim().ToLowerInvariant();

        switch (kind)
        {
            case "local":
            {
                var path = configuration["STATE_LOCAL_PATH"] ?? "data/state.json";
                logger.LogInformation("Using local JSON state store: {Path}", path);
                return new LocalJsonStateStore(path);
            }
            case "appconfig":
            {
                var endpoint = configuration["STATE_APPCONFIG_ENDPOINT"] ?? configuration["AZURE_APP_CONFIGURATION_ENDPOINT"];
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("STATE_APPCONFIG_ENDPOINT (or AZURE_APP_CONFIGURATION_ENDPOINT) is required when STATE_STORE=appconfig.");
                }

                var prefix = configuration["STATE_APPCONFIG_PREFIX"] ?? "foundry-slide-html-generator:";
                logger.LogInformation("Using App Configuration state store: {Endpoint}", endpoint);
                return new AppConfigStateStore(new Uri(endpoint), credential, prefix);
            }
            case "keyvault":
            {
                var vaultUri = configuration["STATE_KEYVAULT_URI"] ?? configuration["AZURE_KEYVAULT_URI"];
                if (string.IsNullOrWhiteSpace(vaultUri))
                {
                    throw new InvalidOperationException("STATE_KEYVAULT_URI (or AZURE_KEYVAULT_URI) is required when STATE_STORE=keyvault.");
                }

                var prefix = configuration["STATE_KEYVAULT_PREFIX"] ?? "foundry-slide-html-generator-";
                logger.LogInformation("Using Key Vault state store: {VaultUri}", vaultUri);
                return new KeyVaultStateStore(new Uri(vaultUri), credential, prefix);
            }
            default:
                throw new InvalidOperationException($"Unknown STATE_STORE: {kind}");
        }
    }
}

