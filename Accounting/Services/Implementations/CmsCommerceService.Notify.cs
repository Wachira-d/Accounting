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
        string? AuthProvider, string? AuthProviderId, int ItemCount, string? PublicToken, Guid OrderId, Guid? CustomerId);

    private Task<OrderNotifyFacts?> LoadOrderNotifyFactsAsync(Guid companyId, Guid siteId, Guid orderId)
        => _db.SiteOrders.AsNoTracking()
            .Where(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId)
            .Select(o => new OrderNotifyFacts(o.OrderNumber, o.Site.Name, o.TotalAmount, o.PaidAmount, o.PaymentTermMode,
                o.DepositRequiredAmount, o.FulfillmentMethod, o.EarliestReadyDate, o.ShippingName, o.ShippingPhone,
                o.ShippingEmail, o.ShippingMethod, o.TrackingNumber,
                o.Customer != null ? o.Customer.FullName : null, o.Customer != null ? o.Customer.Email : null,
                o.Customer != null ? o.Customer.AuthProvider : null, o.Customer != null ? o.Customer.AuthProviderId : null,
                o.Lines.Count, o.PublicToken, o.Id, o.CustomerId))
            .FirstOrDefaultAsync();

    /// <summary>รอบ 210: บรรทัด "ติดตามสถานะคำสั่งซื้อ" ท้ายข้อความถึงลูกค้า — ลิงก์ track-order จากฐานหน้าร้าน (<see cref="CmsStorefrontLink"/>) ·
    /// ประกอบไม่ได้ ⇒ เลขที่ + หน้า "ตรวจสอบการจอง / คำสั่งซื้อ" (ไม่เงียบ)</summary>
    private async Task<CmsNotification> WithOrderLinkAsync(Guid companyId, Guid siteId, OrderNotifyFacts f, CmsNotification message)
    {
        var baseUrl = _shopNotifier == null ? null : await _shopNotifier.StorefrontBaseUrlAsync(companyId, siteId);
        return CmsNotificationText.WithLine(message, CmsStorefrontLink.OrderTrackLine(baseUrl, f.PublicToken, f.OrderNumber));
    }

    private static BookingContact CustomerContactOf(OrderNotifyFacts f)
    {
        // รอบ 211: LINE ที่ลูกค้าผูกเอง (ออเดอร์ > สมาชิก) — BookingNotifier หาให้
        return CmsBookingService.ContactOf(f.ShippingEmail, f.CustomerEmail, f.AuthProvider, f.AuthProviderId,
            CustomerLineTarget.ForOrder(f.OrderId, f.CustomerId));
    }

    private static string? DisplayNameOf(OrderNotifyFacts f) => !string.IsNullOrWhiteSpace(f.CustomerName) ? f.CustomerName : f.ShippingName;

    private static DateOnly? ReadyDateOf(OrderNotifyFacts f) => f.EarliestReadyDate is DateTime d ? DateOnly.FromDateTime(d) : null;

    /// <summary>รอบ 217 (ข้อ 175 ง): จุดเข้าของคิวแจ้งเตือนตามหลัง (<c>DeferredNotifyDispatcher</c>) — เดิม CreateOrderAsync เรียก
    /// <see cref="NotifyOrderCreatedAsync"/> ในคำขอ ⇒ ลูกค้ารอ SMTP/LINE ก่อนเห็นหน้าสำเร็จ ·
    /// ฝ่ายค้าน S4: ถึงคิวแล้วออเดอร์ยกเลิก/คืนเงิน/ถูกลบ ⇒ ไม่ส่ง — คืนเหตุ · ฝ่ายค้าน S3: ข้อยกเว้นโยนออกให้ dispatcher ลองใหม่</summary>
    public async Task<string?> DispatchCreatedNotifyAsync(Guid companyId, Guid siteId, Guid orderId)
    {
        var status = await _db.SiteOrders.AsNoTracking()
            .Where(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId)
            .Select(o => (SiteOrderStatus?)o.Status).FirstOrDefaultAsync();
        if (DeferredNotifyPolicy.OrderSkipReason(status) is string skip) return skip;
        await NotifyOrderCreatedAsync(companyId, siteId, orderId);
        return null;
    }

    /// <summary>ออเดอร์ใหม่: แจ้งร้าน + ยืนยันถึงลูกค้า — รอบ 217 ฝ่ายค้าน S3: ไม่กลืนข้อยกเว้น (ผู้เรียกเดียว = คิวแจ้งเตือนตามหลัง · ถอย/ลองใหม่) ·
    /// ผู้รับที่ส่งไม่ถึงยังประทับบนออเดอร์เหมือนเดิม</summary>
    private async Task NotifyOrderCreatedAsync(Guid companyId, Guid siteId, Guid orderId)
    {
        if (_shopNotifier == null) return;
        var f = await LoadOrderNotifyFactsAsync(companyId, siteId, orderId);
        if (f == null) return;
        var fulfil = FulfillmentMethod.Label(f.FulfillmentMethod ?? FulfillmentMethod.Delivery);
        var now = DateTime.UtcNow;
        var failures = new List<string>();

        var shop = CmsNotificationText.ShopNewOrder(f.SiteName, f.OrderNumber, DisplayNameOf(f), f.ShippingPhone, f.TotalAmount, fulfil, ReadyDateOf(f), f.ItemCount);
        var shopErr = await _shopNotifier.NotifyShopAsync(companyId, siteId, shop, orderEvent: true);
        if (shopErr != null) failures.Add(CmsNotificationText.FailureNote("ร้าน", shop.Subject, shopErr, now));

        var (customer, custErr) = await SendOrderConfirmationToCustomerAsync(companyId, siteId, orderId, f);
        if (custErr != null) failures.Add(CmsNotificationText.FailureNote("ลูกค้า", customer.Subject, custErr, now));

        await StampOrderNotesAsync(companyId, siteId, orderId, failures);
    }

    /// <summary>ยืนยันคำสั่งซื้อถึงลูกค้า (ส่วนลูกค้าของ <see cref="NotifyOrderCreatedAsync"/>) — แยกให้ "ส่งซ้ำ" (รอบ 213) เดินเส้นเดียวกันโดยไม่แจ้งร้านซ้ำ ·
    /// ข้อความประกอบจากสถานะปัจจุบันของคำสั่งซื้อ · คีย์กันซ้ำเดิม</summary>
    private async Task<(CmsNotification Message, string? Error)> SendOrderConfirmationToCustomerAsync(Guid companyId, Guid siteId, Guid orderId, OrderNotifyFacts f)
    {
        var fulfil = FulfillmentMethod.Label(f.FulfillmentMethod ?? FulfillmentMethod.Delivery);
        var due = MtoPaymentTerms.FromSnapshot(f.PaymentTermMode, f.TotalAmount, f.DepositRequiredAmount, f.PaidAmount);
        var shopMessage = await _db.SiteCommerceConfigs.AsNoTracking()
            .Where(c => c.SiteId == siteId && c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => c.OrderConfirmMessageTh).FirstOrDefaultAsync();
        var customer = await WithOrderLinkAsync(companyId, siteId, f,
            CmsNotificationText.CustomerOrderConfirmation(f.SiteName, f.OrderNumber, DisplayNameOf(f), f.TotalAmount,
                due.DueNow, fulfil, ReadyDateOf(f), shopMessage));
        var err = await _shopNotifier!.NotifyCustomerAsync(companyId, siteId, CustomerNotifyEvents.OrderConfirmation,
            CustomerNotifyKeys.Order(orderId, CustomerNotifyEvents.OrderConfirmation), CustomerContactOf(f), customer);
        return (customer, err);
    }

    /// <summary>รอบ 213 (คำตัดสินข้อ 172): ส่งซ้ำข้อความถึงลูกค้าที่ค้างเพราะโควตา — ประกอบใหม่จากสถานะปัจจุบันผ่านเส้นส่งเดิม (ไม่เก็บเนื้อข้อความ) ·
    /// ผู้ตัดสินว่าส่งซ้ำได้ไหม = <see cref="CustomerNotifyResend.Evaluate"/> ที่ผู้เรียก · คืนเหตุเมื่อส่งไม่ถึง (null = ส่งถึง/ไม่ทราบ — ผู้เรียกอ่านสถานะจากแถวบันทึก)</summary>
    public async Task<string?> ResendCustomerNotifyAsync(Guid companyId, Guid orderId, string eventKey)
    {
        if (_shopNotifier == null) return "ระบบส่งข้อความไม่พร้อม";
        var siteId = await _db.SiteOrders.AsNoTracking()
            .Where(o => o.Id == orderId && o.CompanyId == companyId).Select(o => (Guid?)o.SiteId).FirstOrDefaultAsync();
        if (siteId is not Guid sid) return "ไม่พบคำสั่งซื้อ";
        if (eventKey == CustomerNotifyEvents.OrderConfirmation)
        {
            var f = await LoadOrderNotifyFactsAsync(companyId, sid, orderId);
            if (f == null) return "ไม่พบคำสั่งซื้อ";
            var (_, err) = await SendOrderConfirmationToCustomerAsync(companyId, sid, orderId, f);
            return err;
        }
        var status = eventKey switch
        {
            CustomerNotifyEvents.OrderShipped => SiteOrderStatus.Shipped,
            CustomerNotifyEvents.OrderReady => SiteOrderStatus.ReadyForPickup,
            CustomerNotifyEvents.OrderDelivered => SiteOrderStatus.Delivered,
            _ => (SiteOrderStatus?)null,
        };
        if (status is not SiteOrderStatus st) return CustomerNotifyResend.UnsupportedReason(eventKey);
        await NotifyOrderStatusAsync(companyId, sid, orderId, st, stampNotes: false);
        return null;
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
    private async Task NotifyOrderStatusAsync(Guid companyId, Guid siteId, Guid orderId, SiteOrderStatus status, bool stampNotes = true)
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
            var eventKey = CustomerNotifyEvents.OrderStatusKey(status);
            if (msg == null || eventKey == null) return;
            msg = await WithOrderLinkAsync(companyId, siteId, f, msg);
            var err = await _shopNotifier.NotifyCustomerAsync(companyId, siteId, eventKey, CustomerNotifyKeys.Order(orderId, eventKey), CustomerContactOf(f), msg);
            // ฝ่ายค้านรอบ 213 (a): ส่งซ้ำจากหน้าข้อความค้าง ⇒ ไม่ต่อหมายเหตุบนออเดอร์ทุกครั้งที่ลอง
            if (err != null && stampNotes)
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
