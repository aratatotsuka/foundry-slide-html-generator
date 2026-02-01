using Azure;
using Azure.Data.AppConfiguration;
using Azure.Core;

namespace FoundrySlideHtmlGenerator.Backend.State;

public sealed class AppConfigStateStore : IStateStore
{
    private readonly ConfigurationClient _client;
    private readonly string _prefix;

    public AppConfigStateStore(Uri endpoint, TokenCredential credential, string prefix = "foundry-slide-html-generator:")
    {
        _client = new ConfigurationClient(endpoint, credential);
        _prefix = prefix;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var setting = await _client.GetConfigurationSettingAsync(_prefix + key, cancellationToken: cancellationToken);
            return setting.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        => _client.SetConfigurationSettingAsync(_prefix + key, value, cancellationToken: cancellationToken);
}
