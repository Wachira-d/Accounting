using Accounting.Data;
using Accounting.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
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
                await ProcessAbandonedCartsGuarded(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing abandoned carts");
            }

            await Task.Delay(TimeSpan.FromMinutes(60), stoppingToken);
        }
    }

    /// <summary>กันสอง instance ส่งอีเมลตามตะกร้าชุดเดียวกัน (ผลตรวจทีม G · G-01)
    ///
    /// <para>⚠️ ธง <c>AbandonedEmailSentAt</c> เดิมไม่ถูก persist จนกว่าจะจบทั้ง
    /// 100 ใบ ⇒ (ก) สอง instance อ่านชุดเดียวกันแล้วส่งซ้ำทั้งชุด (ข) แม้เครื่อง
    /// เดียว ถ้า crash กลางลูป อีเมลที่ส่งไปแล้วไม่มีร่องรอย รอบหน้าส่งซ้ำหมด ·
    /// และ <c>RemoveRange</c> ของตะกร้าหมดอายุเป็น <b>hard delete</b> ที่รัน
    /// พร้อมกันสองเครื่องได้</para></summary>
    private async Task ProcessAbandonedCartsGuarded(CancellationToken ct)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await Accounting.Helpers.JobLock.RunExclusiveAsync(
            lockDb, Accounting.Helpers.AdvisoryLockKey.BackgroundJob,
            nameof(AbandonedCartService), () => ProcessAbandonedCarts(ct), _logger, ct: ct);
    }

    private async Task ProcessAbandonedCarts(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var emailService = scope.ServiceProvider.GetService<IEmailService>();

        // รอบ 209 P6 (คำตัดสินข้อ 168 ช): ชั่วโมงที่ถือว่าค้างอ่านจากค่าตั้งของแต่ละเว็บ (AbandonedCartHours · ไม่ตั้ง = 3 เดิม) —
        // คัดหยาบด้วยเกณฑ์ต่ำสุด (1 ชม.) แล้วตัดสินรายตะกร้าด้วยค่าตั้งของเว็บนั้น
        var now = DateTime.UtcNow;
        var coarse = now.AddHours(-1);

        // Mark abandoned: carts with items, not abandoned yet, not updated recently, has customer or session
        var staleCarts = await db.SiteCarts
            .Include(c => c.Items).ThenInclude(i => i.SiteProduct).ThenInclude(p => p.Product)
            .Include(c => c.Customer)
            .Include(c => c.Site)
            .Where(c => !c.IsAbandoned
                && c.Items.Any()
                && c.AbandonedEmailSentAt == null
                && (c.UpdatedAt ?? c.CreatedAt) < coarse)
            .OrderBy(c => c.UpdatedAt ?? c.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        var siteIds = staleCarts.Select(c => c.SiteId).Distinct().ToList();
        var configs = await db.Set<Models.Entities.SiteCommerceConfig>().AsNoTracking()
            .Where(c => siteIds.Contains(c.SiteId))
            .Select(c => new { c.SiteId, c.CompanyId, c.EnableAbandonedCartRecovery, c.AbandonedCartHours })
            .ToListAsync(ct);

        var marked = 0;
        foreach (var cart in staleCarts)
        {
            var cfg = configs.FirstOrDefault(c => c.SiteId == cart.SiteId && c.CompanyId == cart.CompanyId);
            var hours = Accounting.Helpers.AbandonedCartEligibility.Hours(cfg?.AbandonedCartHours);
            if ((cart.UpdatedAt ?? cart.CreatedAt) >= now.AddHours(-hours)) continue;   // ยังไม่ถึงเกณฑ์ของเว็บนี้
            cart.IsAbandoned = true;
            marked++;

            // ด่าน PDPA: ร้านเปิดอีเมลกู้ตะกร้า (ไม่มีแถวค่าตั้ง = ค่าเริ่มต้นของ entity = เปิด) **และ** ลูกค้ายินยอมรับข่าวสารการตลาด
            var (send, reason) = Accounting.Helpers.AbandonedCartEligibility.Decide(
                cfg?.EnableAbandonedCartRecovery ?? true, cart.Customer != null, cart.Customer?.Email,
                cart.Customer?.AcceptMarketing == true,
                cart.Customer != null && !cart.Customer.ConsentGiven && cart.Customer.ConsentWithdrawnAt != null);
            if (!send)
            {
                _logger.LogInformation("[{RuleCode}] ไม่ส่งอีเมลกู้ตะกร้า {CartId}: {Reason}",
                    Accounting.Helpers.AbandonedCartEligibility.RuleCode, cart.Id, reason);
                continue;
            }

            if (emailService != null)
            {
                try
                {
                    var lang = ResolveCartLanguage(cart);
                    var subject = lang == "en"
                        ? $"You left items in your cart at {cart.Site.Name}"
                        : $"คุณมีสินค้าค้างในตะกร้าที่ {cart.Site.Name}";
                    var body = BuildRecoveryEmail(cart, lang);
                    await emailService.SendAsync(cart.Customer!.Email, subject, body);
                    cart.AbandonedEmailSentAt = DateTime.UtcNow;
                    // บันทึกทันทีทีละใบ — ธง "ส่งแล้ว" ต้อง durable ก่อนส่งใบถัดไป
                    // ไม่งั้น crash/ct ถูกยกเลิกกลางลูป = ส่งซ้ำทั้งชุดในรอบหน้า
                    await db.SaveChangesAsync(ct);
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

        if (marked > 0 || expired.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private static string ResolveCartLanguage(Models.Entities.SiteCart cart)
    {
        var lang = cart.Site?.DefaultLanguage?.ToLowerInvariant();
        return lang == "en" ? "en" : "th";
    }

    private static string BuildRecoveryEmail(Models.Entities.SiteCart cart, string lang)
    {
        var itemsHtml = string.Join("", cart.Items.Select(i =>
        {
            var name = i.SiteProduct?.DisplayName
                ?? i.SiteProduct?.Product?.Name
                ?? (lang == "en" ? "Item" : "สินค้า");
            return $"<tr><td>{System.Net.WebUtility.HtmlEncode(name)}</td>" +
                   $"<td>{i.Quantity}</td><td>{i.TotalPrice:N2}</td></tr>";
        }));

        if (lang == "en")
        {
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

        return $@"
<!DOCTYPE html>
<html><body style=""font-family:Arial,sans-serif;"">
<h2>คุณมีสินค้าค้างในตะกร้า!</h2>
<p>สวัสดีคุณ {System.Net.WebUtility.HtmlEncode(cart.Customer?.FullName ?? "ลูกค้า")},</p>
<p>คุณมีสินค้ารออยู่ในตะกร้า กรุณาชำระเงินเพื่อสั่งซื้อก่อนของจะหมด!</p>
<table cellpadding=""8"" border=""1"" style=""border-collapse:collapse;"">
<tr><th>สินค้า</th><th>จำนวน</th><th>รวม</th></tr>
{itemsHtml}
</table>
<p><strong>ยอดรวม: {cart.TotalAmount:N2} {cart.Currency}</strong></p>
<p style=""margin-top:20px;"">เยี่ยมชมร้านของเราเพื่อสั่งซื้อให้สำเร็จ</p>
</body></html>";
    }
}
