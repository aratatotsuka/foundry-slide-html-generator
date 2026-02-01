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
    Task<JsonDocument> CreateProjectResponseAsync(JsonDocument requestBody, CancellationToken cancellationToken);

    // Conversations (preview) - required for workflow agents (OnConversationStart triggers etc.)
    Task<string> CreateConversationAsync(JsonDocument requestBody, CancellationToken cancellationToken);

    // Agent Service (v1) - persistent assistants / threads / runs (for Connected Agents).
    Task<IReadOnlyDictionary<string, string>> ListAssistantsByNameAsync(CancellationToken cancellationToken);
    Task<string> CreateAssistantAsync(AssistantDefinition definition, CancellationToken cancellationToken);
    Task UpdateAssistantAsync(string assistantId, AssistantDefinition definition, CancellationToken cancellationToken);
    Task<JsonDocument> CreateThreadAndRunAsync(JsonDocument requestBody, CancellationToken cancellationToken);
    Task<JsonDocument> GetRunAsync(string threadId, string runId, CancellationToken cancellationToken);
    Task<JsonDocument> ListMessagesAsync(string threadId, int limit, string order, CancellationToken cancellationToken);
}
