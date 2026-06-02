#if USE_PUPPETEER
using PuppeteerSharp;
using PuppeteerSharp.Media;
#endif

namespace Accounting.Services.Implementations.Pdf;

/// <summary>
/// Renders an HTML string to a PDF exactly as a browser would — full CSS
/// layout, fonts, colours, images — so the downloaded PDF matches the
/// on-screen preview. Backed by a headless Chromium-family browser
/// (PuppeteerSharp).
///
/// The whole browser integration is guarded by the USE_PUPPETEER compile
/// symbol. Without it (the default) this type compiles to a no-op that
/// reports Enabled=false and returns null, so the app builds with no extra
/// NuGet package and PDFs are produced by QuestPDF. See Accounting.csproj
/// for how to switch it on.
///
/// Even when enabled every entry point is best-effort: a disabled flag, an
/// unavailable browser, or any render error returns null so the caller
/// falls back to QuestPDF. PDF generation can never be broken by this path.
/// </summary>
public interface IHtmlPdfRenderer
{
    /// <summary>True when the renderer is compiled in AND switched on via
    /// config (Pdf:UseHtmlRenderer). Callers skip building data when false.</summary>
    bool Enabled { get; }

    /// <summary>Render HTML → PDF bytes, or null on disabled/failure.</summary>
    Task<byte[]?> TryRenderAsync(string html, CancellationToken ct = default);
}

#if USE_PUPPETEER

public sealed class PuppeteerHtmlPdfRenderer : IHtmlPdfRenderer, IAsyncDisposable
{
    private readonly ILogger<PuppeteerHtmlPdfRenderer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IBrowser? _browser;
    private bool _initFailed;          // latch: stop retrying a broken environment

    public bool Enabled { get; }

    /// <summary>Optional path to an already-installed Chromium-family browser
    /// (e.g. Windows Edge: C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe).
    /// When set, we launch it directly and skip the Chromium download.</summary>
    private readonly string? _executablePath;

    /// <summary>Optional writable folder for PuppeteerSharp's downloaded
    /// Chromium — set this when the app/IIS identity can't write the default
    /// cache location.</summary>
    private readonly string? _browserCachePath;

    public PuppeteerHtmlPdfRenderer(IConfiguration config, ILogger<PuppeteerHtmlPdfRenderer> logger)
    {
        _logger = logger;
        Enabled = config.GetValue("Pdf:UseHtmlRenderer", false);
        _executablePath = config.GetValue<string?>("Pdf:ExecutablePath", null);
        _browserCachePath = config.GetValue<string?>("Pdf:BrowserCachePath", null);
    }

    public async Task<byte[]?> TryRenderAsync(string html, CancellationToken ct = default)
    {
        if (!Enabled || _initFailed || string.IsNullOrWhiteSpace(html)) return null;
        try
        {
            var browser = await GetBrowserAsync(ct);
            if (browser == null) return null;

            var page = await browser.NewPageAsync();
            try
            {
                await page.SetContentAsync(html, new NavigationOptions
                {
                    WaitUntil = new[] { WaitUntilNavigation.Networkidle0 },
                    Timeout = 15000,
                });
                var bytes = await page.PdfDataAsync(new PdfOptions
                {
                    PrintBackground = true,
                    PreferCSSPageSize = true,   // honour the @page size/margins from the template
                    Format = PaperFormat.A4,
                });
                return bytes is { Length: > 0 } ? bytes : null;
            }
            finally
            {
                try { await page.CloseAsync(); } catch { /* page may already be gone */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTML→PDF render failed — falling back to QuestPDF");
            return null;
        }
    }

    private async Task<IBrowser?> GetBrowserAsync(CancellationToken ct)
    {
        if (_browser is { IsConnected: true }) return _browser;
        await _gate.WaitAsync(ct);
        try
        {
            if (_browser is { IsConnected: true }) return _browser;

            var options = new LaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage", "--disable-gpu" },
            };

            if (!string.IsNullOrWhiteSpace(_executablePath))
            {
                options.ExecutablePath = _executablePath;   // installed Edge/Chrome — no download
                _logger.LogInformation("Launching HTML→PDF browser from {Path}", _executablePath);
            }
            else
            {
                var fetcher = string.IsNullOrWhiteSpace(_browserCachePath)
                    ? new BrowserFetcher()
                    : new BrowserFetcher(new BrowserFetcherOptions { Path = _browserCachePath });
                var installed = await fetcher.DownloadAsync();
                if (installed?.GetExecutablePath() is { Length: > 0 } exe)
                    options.ExecutablePath = exe;
                _logger.LogInformation("Using downloaded Chromium for HTML→PDF rendering");
            }

            _browser = await Puppeteer.LaunchAsync(options);
            _logger.LogInformation("Headless browser launched for HTML→PDF rendering");
            return _browser;
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _logger.LogError(ex, "Headless browser init failed — HTML PDF disabled, using QuestPDF");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_browser != null) await _browser.CloseAsync(); }
        catch { /* shutting down — ignore */ }
        _gate.Dispose();
    }
}

#else

/// <summary>No-op implementation compiled when USE_PUPPETEER is not defined.
/// Always disabled, so PdfGenerationService uses QuestPDF.</summary>
public sealed class PuppeteerHtmlPdfRenderer : IHtmlPdfRenderer
{
    public PuppeteerHtmlPdfRenderer(IConfiguration config, ILogger<PuppeteerHtmlPdfRenderer> logger) { }
    public bool Enabled => false;
    public Task<byte[]?> TryRenderAsync(string html, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);
}

#endif
