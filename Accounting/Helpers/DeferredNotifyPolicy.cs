using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 217 (คำตัดสินข้อ 175 ข้อย่อย ง) — <b>ตัวตั้งเดียว</b>ของคิว "แจ้งเตือนตามหลัง" (<see cref="DeferredNotification"/>):
/// เจ้าของรายงาน "กดยืนยันจองแล้วนิ่งไปพักนึง ไม่ขึ้นว่าโหลด" — ต้นเหตุ = คำขอจองรอส่งอีเมล/LINE ถึงลูกค้า + อีเมลถึงร้านทีละผู้รับ
/// (<c>CmsBookingService.NotifyBookingCreatedAsync</c> → <c>CmsShopNotifier.NotifyCustomerAsync/NotifyShopAsync</c> → SMTP/LINE) ก่อนตอบ ·
/// ตอนนี้: เขียนแถวคิวในธุรกรรมเดียวกับการจอง ⇒ ตอบทันที · <c>DeferredNotifyDispatchJob</c> (ทุก <see cref="PollInterval"/> · JobLock) ส่งตามหลัง ·
/// ส่งถึง/ไม่ถึง = CustomerNotifyLogs + หมายเหตุภายในของรายการ (เส้นเดิม ไม่เงียบ) · ข้อยกเว้นระหว่างเดินคิว ⇒ ถอย
/// Backoff แล้วลองใหม่ ≤ <see cref="MaxAttempts"/> ครั้ง จากนั้นปิดแถวพร้อม <c>LastError</c> (log Error)
/// </summary>
public static class DeferredNotifyPolicy
{
    public const string BookingCreated = "booking.created";
    public const string OrderCreated = "order.created";
    public const int MaxAttempts = 5;
    public const int BatchSize = 50;
    public const int MaxErrorLength = 500;
    /// <summary>ความถี่เดินคิว — ลูกค้าได้ข้อความยืนยันภายในราวนี้หลังกดจอง</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    /// <summary>ระยะ lease ของแถวที่หยิบไปแล้ว (เครื่องล้มกลางทาง ⇒ หมด lease แล้วหยิบใหม่ได้)</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public static bool IsKnownKind(string? kind) => kind is BookingCreated or OrderCreated;

    /// <summary>แถวคิวใหม่ — ผู้เรียก Add เข้า DbContext <b>ก่อน SaveChanges/Commit ของรายการเดียวกัน</b> (หนึ่งธุรกรรม)</summary>
    public static DeferredNotification New(Guid companyId, Guid siteId, string kind, Guid entityId, DateTime nowUtc)
    {
        if (!IsKnownKind(kind)) throw new ArgumentException($"ไม่รู้จักชนิดคิวแจ้งเตือน '{kind}'", nameof(kind));
        return new DeferredNotification { CompanyId = companyId, SiteId = siteId, Kind = kind, EntityId = entityId, DueAt = nowUtc, CreatedBy = "system" };
    }

    /// <summary>ถอยหลังล้มครั้งที่ n (1-based): 30 วิ · 1 · 2 · 4 · 8 นาที</summary>
    private static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Clamp(attempts, 1, MaxAttempts) - 1));

    /// <summary>หลังล้ม: ปิดแถว (เกินจำนวนครั้ง) หรือเลื่อนเวลาลองใหม่</summary>
    public static (bool GiveUp, DateTime NextDueAt) AfterFailure(int attempts, DateTime nowUtc)
        => attempts >= MaxAttempts ? (true, nowUtc) : (false, nowUtc + Backoff(attempts));

    /// <summary>ฝ่ายค้าน S4: ถึงคิวแล้วการจองไม่ควรได้ "ยืนยันการจอง"/ร้านไม่ควรได้ "การจองใหม่" — ยกเลิก · ไม่มา · ถูกลบ ⇒ เหตุ (ปิดแถวพร้อมหมายเหตุ) ·
    /// null = ส่งตามปกติ (รอยืนยัน · ยืนยันแล้ว · คิวสำรอง · กำลังให้บริการ · เสร็จแล้ว — ข้อความประกอบจากสถานะปัจจุบัน)</summary>
    public static string? BookingSkipReason(BookingStatus? status) => status switch
    {
        null => "ไม่ส่ง: ไม่พบการจอง (ถูกลบก่อนถึงคิวแจ้งเตือน)",
        BookingStatus.Cancelled => "ไม่ส่ง: การจองถูกยกเลิกก่อนถึงคิวแจ้งเตือน (ไม่ส่ง \"ยืนยันการจอง\"/\"การจองใหม่\")",
        BookingStatus.NoShow => "ไม่ส่ง: การจองถูกบันทึกว่าไม่มาก่อนถึงคิวแจ้งเตือน",
        _ => null,
    };

    /// <summary>ฝ่ายค้าน S4 (ออเดอร์): ยกเลิก · คืนเงินเต็ม · ถูกลบ ⇒ ไม่ส่ง "ออเดอร์ใหม่"/"ยืนยันคำสั่งซื้อ" · คืนเงินบางส่วนยังส่ง (ออเดอร์ยังเดินอยู่)</summary>
    public static string? OrderSkipReason(SiteOrderStatus? status) => status switch
    {
        null => "ไม่ส่ง: ไม่พบคำสั่งซื้อ (ถูกลบก่อนถึงคิวแจ้งเตือน)",
        SiteOrderStatus.Cancelled => "ไม่ส่ง: คำสั่งซื้อถูกยกเลิกก่อนถึงคิวแจ้งเตือน",
        SiteOrderStatus.Refunded => "ไม่ส่ง: คำสั่งซื้อคืนเงินแล้วก่อนถึงคิวแจ้งเตือน",
        _ => null,
    };

    public static string Truncate(string? error)
    {
        var e = string.IsNullOrWhiteSpace(error) ? "ไม่ทราบสาเหตุ" : error.Trim();
        return e.Length > MaxErrorLength ? e[..MaxErrorLength] : e;
    }
}
