using Microsoft.Playwright;
using FoundrySlideHtmlGenerator.Backend.Orchestration;

namespace FoundrySlideHtmlGenerator.Backend.Rendering;

public sealed class PlaywrightPngRenderer : IPngRenderer, IAsyncDisposable
{
    private readonly ILogger<PlaywrightPngRenderer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public PlaywrightPngRenderer(ILogger<PlaywrightPngRenderer> logger)
    {
        _logger = logger;
    }

    public async Task<byte[]> RenderAsync(string html, string aspect, CancellationToken cancellationToken)
    {
        var (width, height, _) = AspectPrompt.GetCanvas(aspect);
        await EnsureBrowserAsync(cancellationToken);

        if (_browser is null)
        {
            throw new InvalidOperationException("Playwright browser is not initialized.");
        }

        var page = await _browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height }
        });

        try
        {
            await page.SetContentAsync(html, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            });

            // Allow layout/fonts to settle.
            await page.WaitForTimeoutAsync(100);

            return await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                FullPage = true
            });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_browser is not null)
            {
                return;
            }

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--disable-dev-shm-usage"]
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        _gate.Dispose();
    }
}
