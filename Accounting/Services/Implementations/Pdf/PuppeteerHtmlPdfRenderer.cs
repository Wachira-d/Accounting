using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace Accounting.Services.Implementations.Pdf;

/// <summary>
/// Renders an HTML string to a PDF exactly as a browser would — full CSS
/// layout, fonts, colours, images — so the downloaded PDF matches the
/// on-screen preview. Implemented with a headless Chromium (PuppeteerSharp).
///
/// Every entry point is best-effort: when disabled, when the browser can't
/// be obtained, or on any render error, it returns null so the caller falls
/// back to the always-available QuestPDF renderer. PDF generation can never
/// be broken by this path.
/// </summary>
public interface IHtmlPdfRenderer
{
    /// <summary>True when the renderer is switched on via config
    /// (Pdf:UseHtmlRenderer). Lets callers skip building data when off.</summary>
    bool Enabled { get; }

    /// <summary>Render HTML → PDF bytes, or null on disabled/failure.</summary>
    Task<byte[]?> TryRenderAsync(string html, CancellationToken ct = default);
}

public sealed class PuppeteerHtmlPdfRenderer : IHtmlPdfRenderer, IAsyncDisposable
{
    private readonly ILogger<PuppeteerHtmlPdfRenderer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IBrowser? _browser;
    private bool _initFailed;          // latch: stop retrying a broken environment

    public bool Enabled { get; }

    public PuppeteerHtmlPdfRenderer(IConfiguration config, ILogger<PuppeteerHtmlPdfRenderer> logger)
    {
        _logger = logger;
        // Default OFF — the headless browser needs Chromium + native libs on the
        // host, so it's opt-in. Flip Pdf:UseHtmlRenderer=true once the server is
        // provisioned; until then the app keeps using QuestPDF unchanged.
        Enabled = config.GetValue("Pdf:UseHtmlRenderer", false);
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
                    // Wait for fonts/images to settle; cap so a hung resource can't stall.
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

            // Download a matching Chromium on first use (cached afterwards).
            await new BrowserFetcher().DownloadAsync();

            _browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                // --no-sandbox is required to run Chromium as root in most
                // containers; the others avoid /dev/shm and GPU issues on servers.
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage", "--disable-gpu" },
            });
            _logger.LogInformation("Headless Chromium launched for HTML→PDF rendering");
            return _browser;
        }
        catch (Exception ex)
        {
            _initFailed = true;   // don't hammer a broken environment on every request
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
