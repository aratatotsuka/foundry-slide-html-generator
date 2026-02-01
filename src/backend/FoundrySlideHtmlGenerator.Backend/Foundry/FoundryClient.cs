using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace FoundrySlideHtmlGenerator.Backend.Foundry;

// Azure AI Foundry (Project endpoint) REST client.
//
// Key requirements:
// - Auth: Azure.Identity DefaultAzureCredential -> Bearer token scope https://ai.azure.com/.default
// - API version: 2025-11-15-preview (configurable)
// - Uses /openai/responses compatibility route for model invocation
// - Uses /agents for agent provisioning
// - Uses OpenAI-compatible /openai/files + /openai/vector_stores for file_search
public sealed class FoundryClient : IFoundryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly FoundryOptions _options;
    private readonly TokenCredential _credential;
    private readonly ILogger<FoundryClient> _logger;

    private AccessToken? _cachedToken;

    public FoundryClient(
        HttpClient httpClient,
        IOptions<FoundryOptions> options,
        TokenCredential credential,
        ILogger<FoundryClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _credential = credential;
        _logger = logger;

        _httpClient.Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds);
    }

    public async Task<IReadOnlyDictionary<string, string>> ListAgentsByNameAsync(CancellationToken cancellationToken)
    {
        var uri = WithApiVersion(BuildProjectUri("agents"));
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var json = await ReadJsonAsync(response, cancellationToken);
        var root = json.RootElement;

        var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array
            ? dataProp
            : root.ValueKind == JsonValueKind.Array
                ? root
                : default;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var agent in data.EnumerateArray())
        {
            if (!agent.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? name = null;
            if (agent.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            {
                name = nameProp.GetString();
            }
            else if (agent.TryGetProperty("definition", out var definitionProp)
                     && definitionProp.ValueKind == JsonValueKind.Object
                     && definitionProp.TryGetProperty("name", out var nestedNameProp)
                     && nestedNameProp.ValueKind == JsonValueKind.String)
            {
                name = nestedNameProp.GetString();
            }

            var id = idProp.GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            map[name] = id;
        }

        return map;
    }

    public async Task<JsonDocument> GetAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        var uri = WithApiVersion(BuildProjectUri($"agents/{agentId}"));
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    public async Task<string> CreateAgentAsync(AgentDefinition definition, CancellationToken cancellationToken)
    {
        var uri = WithApiVersion(BuildProjectUri("agents"));
        var promptDefinition = new
        {
            kind = "prompt",
            model = _options.ModelDeploymentName,
            instructions = definition.Instructions,
            tools = definition.Tools
        };

        var body = JsonSerializer.Serialize(new { name = definition.Name, definition = promptDefinition }, JsonOptions);

        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var json = await ReadJsonAsync(response, cancellationToken);
        if (json.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
        {
            return idProp.GetString()!;
        }

        throw new InvalidOperationException("CreateAgentAsync: response missing id.");
    }

    public async Task UpdateAgentAsync(string agentId, AgentDefinition definition, CancellationToken cancellationToken)
    {
        var uri = WithApiVersion(BuildProjectUri($"agents/{agentId}"));
        var promptDefinition = new
        {
            kind = "prompt",
            model = _options.ModelDeploymentName,
            instructions = definition.Instructions,
            tools = definition.Tools
        };

        var body = JsonSerializer.Serialize(new { definition = promptDefinition }, JsonOptions);

        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<string> UploadFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var uri = WithApiVersion(BuildProjectUri("openai/files"));
        using var response = await SendWithRetryAsync(() =>
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent("assistants"), "purpose");

            var fileStream = File.OpenRead(filePath);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "file", Path.GetFileName(filePath));

            return new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
        }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var json = await ReadJsonAsync(response, cancellationToken);
        if (json.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
        {
            return idProp.GetString()!;
        }

        throw new InvalidOperationException("UploadFileAsync: response missing id.");
    }

    public async Task<string> CreateVectorStoreAsync(string name, IReadOnlyList<string> fileIds, CancellationToken cancellationToken)
    {
        var uri = WithApiVersion(BuildProjectUri("openai/vector_stores"));
        var body = JsonSerializer.Serialize(new
        {
            name,
            file_ids = fileIds
        }, JsonOptions);

        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var json = await ReadJsonAsync(response, cancellationToken);
        if (json.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
        {
            return idProp.GetString()!;
        }

        throw new InvalidOperationException("CreateVectorStoreAsync: response missing id.");
    }

    public async Task WaitForVectorStoreReadyAsync(string vectorStoreId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var uri = WithApiVersion(BuildProjectUri($"openai/vector_stores/{vectorStoreId}"));
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            using var json = await ReadJsonAsync(response, cancellationToken);
            if (json.RootElement.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String)
            {
                var status = statusProp.GetString();
                if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        _logger.LogWarning("Vector store {VectorStoreId} not ready after {Timeout}.", vectorStoreId, timeout);
    }

    public async Task<JsonDocument> CreateResponseAsync(JsonDocument requestBody, CancellationToken cancellationToken)
    {
        var uri = WithApiVersion(BuildExecutionUri("openai/responses"));
        var payload = requestBody.RootElement.GetRawText();
        var sw = Stopwatch.StartNew();
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        }, cancellationToken);
        sw.Stop();

        _logger.LogInformation("Foundry /openai/responses {StatusCode} in {ElapsedMs}ms", (int)response.StatusCode, sw.ElapsedMilliseconds);

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private Uri BuildProjectUri(string relativePath) => Combine(_options.ProjectEndpoint, relativePath);

    private Uri BuildExecutionUri(string relativePath)
    {
        if (_options.UseAgentApplication)
        {
            if (string.IsNullOrWhiteSpace(_options.ApplicationEndpoint))
            {
                throw new InvalidOperationException("USE_AGENT_APPLICATION=true but FOUNDRY_APPLICATION_ENDPOINT is not set.");
            }

            return Combine(_options.ApplicationEndpoint, relativePath);
        }

        return BuildProjectUri(relativePath);
    }

    private static Uri EnsureTrailingSlash(string baseUri)
    {
        var trimmed = baseUri.TrimEnd('/') + "/";
        return new Uri(trimmed, UriKind.Absolute);
    }

    private static Uri Combine(string baseEndpoint, string relativePath)
    {
        var baseUri = EnsureTrailingSlash(baseEndpoint);

        // Be tolerant if the configured endpoint already includes "/openai".
        if (baseUri.AbsolutePath.EndsWith("/openai/", StringComparison.OrdinalIgnoreCase)
            && relativePath.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = relativePath["openai/".Length..];
        }

        return new Uri(baseUri, relativePath);
    }

    private Uri WithApiVersion(Uri uri)
    {
        var builder = new UriBuilder(uri);
        var query = builder.Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            builder.Query = $"api-version={Uri.EscapeDataString(_options.ApiVersion)}";
        }
        else if (!query.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
        {
            builder.Query = query.TrimStart('?') + "&api-version=" + Uri.EscapeDataString(_options.ApiVersion);
        }

        return builder.Uri;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        var maxAttempts = 6;
        var delay = TimeSpan.FromMilliseconds(500);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = createRequest();
            await AttachAuthAsync(request, cancellationToken);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(ex, "HTTP send failed (attempt {Attempt}/{Max}). Retrying in {Delay}.", attempt, maxAttempts, delay);
                await Task.Delay(Jitter(delay), cancellationToken);
                delay = delay * 2;
                continue;
            }

            if (response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500)
            {
                if (attempt >= maxAttempts)
                {
                    return response;
                }

                var retryAfter = response.Headers.RetryAfter?.Delta;
                var wait = retryAfter ?? delay;
                _logger.LogWarning("HTTP {StatusCode} (attempt {Attempt}/{Max}). Retrying in {Delay}.", (int)response.StatusCode, attempt, maxAttempts, wait);

                response.Dispose();
                await Task.Delay(Jitter(wait), cancellationToken);
                delay = delay * 2;
                continue;
            }

            return response;
        }

        throw new InvalidOperationException("SendWithRetryAsync: unreachable.");
    }

    private static TimeSpan Jitter(TimeSpan delay)
    {
        var ms = delay.TotalMilliseconds;
        var jitter = Random.Shared.NextDouble() * (ms * 0.2);
        return TimeSpan.FromMilliseconds(ms + jitter);
    }

    private async Task AttachAuthAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cachedToken is { } cached && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.Token;
        }

        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://ai.azure.com/.default" }),
            cancellationToken);

        _cachedToken = token;
        return token.Token;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var payload = response.Content is not null ? await response.Content.ReadAsStringAsync(cancellationToken) : "";
        throw new HttpRequestException($"Foundry API request failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {payload}");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
