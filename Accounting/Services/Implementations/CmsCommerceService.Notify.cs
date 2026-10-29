using Accounting.Helpers;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 P3 — แจ้งเตือนของออเดอร์หน้าเว็บ (R-209-A4/B5/E2 · คำตัดสินข้อ 168 ซ) ผ่าน <see cref="Interfaces.ICmsShopNotifier"/> (ช่องเดิม <c>IBookingNotifier</c>)
/// <list type="bullet">
///   <item>ร้าน: ออเดอร์ใหม่ · ลูกค้าแนบสลิป — เคารพ <c>SiteCommerceConfig.NotifyOnNewOrder</c> · ผู้รับ <c>OrderNotificationEmails</c> → อีเมลเว็บ → อีเมลบริษัท</item>
///   <item>ลูกค้า: ยืนยันคำสั่งซื้อ (+ <c>OrderConfirmMessageTh</c>) · จัดส่งแล้ว (+ ขนส่ง/เลขพัสดุ) · พร้อมให้รับ · ส่งมอบแล้ว</item>
/// </list>
/// เรียก<b>หลังบันทึก</b>เสมอ · ส่งไม่ถึง ⇒ ประทับ <see cref="CmsNotificationText.FailureTag"/> บนหมายเหตุภายในของออเดอร์ (ไม่ล้มคำสั่ง · ไม่เงียบ — F2 #7) ·
/// ข้อความทั้งหมดจาก <see cref="CmsNotificationText"/> ตัวเดียว
/// </summary>
public partial class CmsCommerceService
{
    private sealed record OrderNotifyFacts(string OrderNumber, string SiteName, decimal TotalAmount, decimal PaidAmount, string? PaymentTermMode,
        decimal DepositRequiredAmount, string? FulfillmentMethod, DateTime? EarliestReadyDate, string? ShippingName, string? ShippingPhone,
        string? ShippingEmail, string? ShippingMethod, string? TrackingNumber, string? CustomerName, string? CustomerEmail,
        string? AuthProvider, string? AuthProviderId, int ItemCount);

    private Task<OrderNotifyFacts?> LoadOrderNotifyFactsAsync(Guid companyId, Guid siteId, Guid orderId)
        => _db.SiteOrders.AsNoTracking()
            .Where(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId)
            .Select(o => new OrderNotifyFacts(o.OrderNumber, o.Site.Name, o.TotalAmount, o.PaidAmount, o.PaymentTermMode,
                o.DepositRequiredAmount, o.FulfillmentMethod, o.EarliestReadyDate, o.ShippingName, o.ShippingPhone,
                o.ShippingEmail, o.ShippingMethod, o.TrackingNumber,
                o.Customer != null ? o.Customer.FullName : null, o.Customer != null ? o.Customer.Email : null,
                o.Customer != null ? o.Customer.AuthProvider : null, o.Customer != null ? o.Customer.AuthProviderId : null,
                o.Lines.Count))
            .FirstOrDefaultAsync();

    private static BookingContact CustomerContactOf(OrderNotifyFacts f)
        => CmsBookingService.ContactOf(f.ShippingEmail, f.CustomerEmail, f.AuthProvider, f.AuthProviderId);

    private static string? DisplayNameOf(OrderNotifyFacts f) => !string.IsNullOrWhiteSpace(f.CustomerName) ? f.CustomerName : f.ShippingName;

    private static DateOnly? ReadyDateOf(OrderNotifyFacts f) => f.EarliestReadyDate is DateTime d ? DateOnly.FromDateTime(d) : null;

    /// <summary>ออเดอร์ใหม่: แจ้งร้าน + ยืนยันถึงลูกค้า</summary>
    private async Task NotifyOrderCreatedAsync(Guid companyId, Guid siteId, Guid orderId)
    {
        if (_shopNotifier == null) return;
        try
        {
            var f = await LoadOrderNotifyFactsAsync(companyId, siteId, orderId);
            if (f == null) return;
            var fulfil = FulfillmentMethod.Label(f.FulfillmentMethod ?? FulfillmentMethod.Delivery);
            var due = MtoPaymentTerms.FromSnapshot(f.PaymentTermMode, f.TotalAmount, f.DepositRequiredAmount, f.PaidAmount);
            var shopMessage = await _db.SiteCommerceConfigs.AsNoTracking()
                .Where(c => c.SiteId == siteId && c.CompanyId == companyId && !c.IsDeleted)
                .Select(c => c.OrderConfirmMessageTh).FirstOrDefaultAsync();
            var now = DateTime.UtcNow;
            var failures = new List<string>();

            var shop = CmsNotificationText.ShopNewOrder(f.SiteName, f.OrderNumber, DisplayNameOf(f), f.ShippingPhone, f.TotalAmount, fulfil, ReadyDateOf(f), f.ItemCount);
            var shopErr = await _shopNotifier.NotifyShopAsync(companyId, siteId, shop, orderEvent: true);
            if (shopErr != null) failures.Add(CmsNotificationText.FailureNote("ร้าน", shop.Subject, shopErr, now));

            var customer = CmsNotificationText.CustomerOrderConfirmation(f.SiteName, f.OrderNumber, DisplayNameOf(f), f.TotalAmount,
                due.DueNow, fulfil, ReadyDateOf(f), shopMessage);
            var custErr = await _shopNotifier.NotifyCustomerAsync(companyId, CustomerContactOf(f), customer);
            if (custErr != null) failures.Add(CmsNotificationText.FailureNote("ลูกค้า", customer.Subject, custErr, now));

            await StampOrderNotesAsync(companyId, siteId, orderId, failures);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // คำสั่งซื้อบันทึกแล้ว — ห้ามให้ขั้นแจ้งเตือนทำให้ลูกค้าเห็น "สั่งไม่สำเร็จ" แล้วสั่งซ้ำ · ล้มดังที่บันทึกระบบระดับ Error
            _logger.LogError(ex, "แจ้งเตือนออเดอร์ใหม่ {OrderId} ล้ม (company {CompanyId})", orderId, companyId);
        }
    }

    /// <summary>ลูกค้าแนบสลิป: แจ้งร้านให้ตรวจ</summary>
    private async Task NotifySlipUploadedAsync(Guid companyId, Guid siteId, Guid orderId, decimal amount)
    {
        if (_shopNotifier == null) return;
        try
        {
            var f = await LoadOrderNotifyFactsAsync(companyId, siteId, orderId);
            if (f == null) return;
            var msg = CmsNotificationText.ShopSlipUploaded(f.SiteName, f.OrderNumber, DisplayNameOf(f), amount);
            var err = await _shopNotifier.NotifyShopAsync(companyId, siteId, msg, orderEvent: true);
            if (err != null)
                await StampOrderNotesAsync(companyId, siteId, orderId, new[] { CmsNotificationText.FailureNote("ร้าน", msg.Subject, err, DateTime.UtcNow) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งร้านเรื่องสลิปของออเดอร์ {OrderId} ล้ม (company {CompanyId})", orderId, companyId);
        }
    }

    /// <summary>สถานะที่ลูกค้าควรรู้ (จัดส่งแล้ว · พร้อมให้รับ · ส่งมอบแล้ว) — สถานะอื่นไม่ส่ง</summary>
    private async Task NotifyOrderStatusAsync(Guid companyId, Guid siteId, Guid orderId, SiteOrderStatus status)
    {
        if (_shopNotifier == null) return;
        try
        {
            var f = await LoadOrderNotifyFactsAsync(companyId, siteId, orderId);
            if (f == null) return;
            var due = MtoPaymentTerms.FromSnapshot(f.PaymentTermMode, f.TotalAmount, f.DepositRequiredAmount, f.PaidAmount);
            // ผู้ขนส่ง: ชื่อวิธีจัดส่งที่ลูกค้าเลือกตอนสั่ง (อัตราค่าส่ง) — ฐานรอบนี้ยังไม่มีช่องผู้ขนส่งแยก (ทีม P1 · B4)
            var carrier = f.FulfillmentMethod == FulfillmentMethod.Pickup ? null : f.ShippingMethod;
            var msg = CmsNotificationText.CustomerOrderStatus(status, f.SiteName, f.OrderNumber, DisplayNameOf(f), carrier, f.TrackingNumber, due.Balance);
            if (msg == null) return;
            var err = await _shopNotifier.NotifyCustomerAsync(companyId, CustomerContactOf(f), msg);
            if (err != null)
                await StampOrderNotesAsync(companyId, siteId, orderId, new[] { CmsNotificationText.FailureNote("ลูกค้า", msg.Subject, err, DateTime.UtcNow) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งลูกค้าเรื่องสถานะ {Status} ของออเดอร์ {OrderId} ล้ม (company {CompanyId})", status, orderId, companyId);
        }
    }

    /// <summary>ประทับเหตุที่แจ้งไม่ถึงบนออเดอร์ (ต่อท้าย ไม่ทับ · ซ้ำไม่ต่อซ้ำ)</summary>
    private async Task StampOrderNotesAsync(Guid companyId, Guid siteId, Guid orderId, IReadOnlyCollection<string> notes)
    {
        if (notes.Count == 0) return;
        var order = await _db.SiteOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId);
        if (order == null) return;
        foreach (var n in notes) order.InternalNotes = DepositPolicyResolver.AppendNoteOnce(order.InternalNotes, n);
        await _db.SaveChangesAsync();
    }
}
