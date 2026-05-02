using Accounting.Data;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// Background service that detects and flags abandoned carts, then sends recovery emails.
/// Runs every 60 minutes.
/// </summary>
public class AbandonedCartService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AbandonedCartService> _logger;

    public AbandonedCartService(IServiceScopeFactory scopeFactory, ILogger<AbandonedCartService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AbandonedCartService started");
        // Wait 5 min on startup so DB is ready
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAbandonedCarts(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing abandoned carts");
            }

            await Task.Delay(TimeSpan.FromMinutes(60), stoppingToken);
        }
    }

    private async Task ProcessAbandonedCarts(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var emailService = scope.ServiceProvider.GetService<IEmailService>();

        var threshold = DateTime.UtcNow.AddHours(-3); // carts inactive for 3+ hours

        // Mark abandoned: carts with items, not abandoned yet, not updated recently, has customer or session
        var staleCarts = await db.SiteCarts
            .Include(c => c.Items)
            .Include(c => c.Customer)
            .Include(c => c.Site)
            .Where(c => !c.IsAbandoned
                && c.Items.Any()
                && c.AbandonedEmailSentAt == null
                && (c.UpdatedAt ?? c.CreatedAt) < threshold)
            .Take(100)
            .ToListAsync(ct);

        foreach (var cart in staleCarts)
        {
            cart.IsAbandoned = true;

            if (cart.Customer != null && !string.IsNullOrEmpty(cart.Customer.Email) && emailService != null)
            {
                try
                {
                    var subject = $"You left items in your cart at {cart.Site.Name}";
                    var body = BuildRecoveryEmail(cart);
                    await emailService.SendAsync(cart.Customer.Email, subject, body);
                    cart.AbandonedEmailSentAt = DateTime.UtcNow;
                    _logger.LogInformation("Abandoned cart recovery email sent to {Email}", cart.Customer.Email);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send abandoned cart email for cart {CartId}", cart.Id);
                }
            }
        }

        // Cleanup expired carts (>30 days abandoned, no customer)
        var expiry = DateTime.UtcNow.AddDays(-30);
        var expired = await db.SiteCarts
            .Where(c => c.IsAbandoned && c.CustomerId == null && c.CreatedAt < expiry)
            .Take(500)
            .ToListAsync(ct);
        if (expired.Any())
        {
            db.SiteCarts.RemoveRange(expired);
            _logger.LogInformation("Cleaned up {Count} expired guest carts", expired.Count);
        }

        if (staleCarts.Count > 0 || expired.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private static string BuildRecoveryEmail(Models.Entities.SiteCart cart)
    {
        var itemsHtml = string.Join("", cart.Items.Select(i =>
            $"<tr><td>{System.Net.WebUtility.HtmlEncode(i.SiteProduct?.DisplayName ?? "Item")}</td>" +
            $"<td>{i.Quantity}</td><td>{i.TotalPrice:N2}</td></tr>"));

        return $@"
<!DOCTYPE html>
<html><body style=""font-family:Arial,sans-serif;"">
<h2>You left items in your cart!</h2>
<p>Hi {System.Net.WebUtility.HtmlEncode(cart.Customer?.FullName ?? "there")},</p>
<p>You have items waiting in your shopping cart. Complete your purchase before they're gone!</p>
<table cellpadding=""8"" border=""1"" style=""border-collapse:collapse;"">
<tr><th>Product</th><th>Qty</th><th>Total</th></tr>
{itemsHtml}
</table>
<p><strong>Total: {cart.TotalAmount:N2} {cart.Currency}</strong></p>
<p style=""margin-top:20px;"">Visit our store to complete your purchase.</p>
</body></html>";
    }
}
