using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;

namespace FoundrySlideHtmlGenerator.Backend.State;

public sealed class KeyVaultStateStore : IStateStore
{
    private readonly SecretClient _client;
    private readonly string _prefix;

    public KeyVaultStateStore(Uri vaultUri, TokenCredential credential, string prefix = "foundry-slide-html-generator-")
    {
        _client = new SecretClient(vaultUri, credential);
        _prefix = prefix;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var secret = await _client.GetSecretAsync(_prefix + key, cancellationToken: cancellationToken);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        => _client.SetSecretAsync(_prefix + key, value, cancellationToken);
}

