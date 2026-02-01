namespace FoundrySlideHtmlGenerator.Backend.Rendering;

public interface IPngRenderer
{
    Task<byte[]> RenderAsync(string html, string aspect, CancellationToken cancellationToken);
}

