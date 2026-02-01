using System.Text.Json;

namespace FoundrySlideHtmlGenerator.Backend.Foundry;

public interface IFoundryClient
{
    Task<IReadOnlyDictionary<string, string>> ListAgentsByNameAsync(CancellationToken cancellationToken);
    Task<JsonDocument> GetAgentAsync(string agentId, CancellationToken cancellationToken);
    Task<string> CreateAgentAsync(AgentDefinition definition, CancellationToken cancellationToken);
    Task UpdateAgentAsync(string agentId, AgentDefinition definition, CancellationToken cancellationToken);

    Task<string> UploadFileAsync(string filePath, CancellationToken cancellationToken);
    Task<string> CreateVectorStoreAsync(string name, IReadOnlyList<string> fileIds, CancellationToken cancellationToken);
    Task WaitForVectorStoreReadyAsync(string vectorStoreId, TimeSpan timeout, CancellationToken cancellationToken);

    Task<JsonDocument> CreateResponseAsync(JsonDocument requestBody, CancellationToken cancellationToken);
}
