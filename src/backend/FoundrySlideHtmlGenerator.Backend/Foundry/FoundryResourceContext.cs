namespace FoundrySlideHtmlGenerator.Backend.Foundry;

public sealed class FoundryResourceContext
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? VectorStoreId { get; set; }
    public Dictionary<string, string> AgentIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task Ready => _ready.Task;

    public void MarkReady() => _ready.TrySetResult();
    public void MarkFailed(Exception ex) => _ready.TrySetException(ex);
}

