using Accounting.Helpers;

namespace Accounting.Services.Interfaces;

/// <summary>
/// ตัวกลางแจ้งเตือนของหน้าเว็บ (ร้านค้า · การจอง · lead) — รอบ 209 P3 (R-209-D5 · คำตัดสินข้อ 168 ซ) ·
/// <b>ไม่ใช่ผู้ให้บริการใหม่</b>: ส่งผ่านช่องเดิม <see cref="IBookingNotifier"/> (อีเมล → LINE) ทุกฉบับ ·
/// <b>ไม่โยน</b> — คืนเหตุที่ส่งไม่ถึง (null = ถึง/ปิดไว้ตามค่าตั้ง) ให้ผู้เรียกประทับบนรายการ (F2 #7) · ทุกความล้มเหลว log ระดับ Error
/// </summary>
public interface ICmsShopNotifier
{
    /// <summary>แจ้งร้าน — ผู้รับตาม <see cref="CmsNotificationText.ShopRecipients"/> · <paramref name="orderEvent"/> = เรื่องออเดอร์ (เคารพ <c>NotifyOnNewOrder</c>)</summary>
    Task<string?> NotifyShopAsync(Guid companyId, Guid? siteId, CmsNotification message, bool orderEvent, CancellationToken ct = default);

    /// <summary>แจ้งลูกค้า — ช่องทางจาก <paramref name="contact"/> (อีเมล/LINE ของรายการ) · รอบ 212: <paramref name="eventKey"/> (<see cref="CustomerNotifyEvents"/>) ·
    /// <paramref name="messageKey"/> (<see cref="CustomerNotifyKeys"/>) ส่งต่อให้ <c>IBookingNotifier</c> ตัดสินค่าตั้ง/โควตา/กันซ้ำ · ระงับตามค่าตั้ง/โควตาเต็ม ⇒ คืนเหตุเช่นกัน</summary>
    Task<string?> NotifyCustomerAsync(Guid companyId, Guid? siteId, string eventKey, string messageKey, BookingContact contact, CmsNotification message,
        CancellationToken ct = default);

    /// <summary>รอบ 210: ฐาน URL หน้าร้านของเว็บ (<see cref="CmsStorefrontLink.BaseUrl"/>) สำหรับลิงก์จัดการการจอง/ติดตามคำสั่งซื้อในข้อความถึงลูกค้า ·
    /// null = ประกอบไม่ได้ (ผู้เรียกใส่เลขที่ + หน้า "ตรวจสอบการจอง / คำสั่งซื้อ" แทน — ไม่เงียบ) · ไม่โยน</summary>
    Task<string?> StorefrontBaseUrlAsync(Guid companyId, Guid siteId, CancellationToken ct = default);
}
