using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace FoundrySlideHtmlGenerator.Backend.Jobs;

public sealed class HtmlDownloadOptions
{
    [ConfigurationKeyName("ALLOW_HTML_DOWNLOAD")]
    public bool AllowHtmlDownload { get; init; } = false;

    // Optional API key. If set, client must send header: X-Download-Key: {key}
    [ConfigurationKeyName("HTML_DOWNLOAD_API_KEY")]
    public string? DownloadApiKey { get; init; }
}

