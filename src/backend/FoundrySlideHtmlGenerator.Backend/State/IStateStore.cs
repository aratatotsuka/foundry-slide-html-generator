namespace FoundrySlideHtmlGenerator.Backend.State;

public interface IStateStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}

