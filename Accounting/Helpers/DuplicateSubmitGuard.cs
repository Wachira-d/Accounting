namespace Accounting.Helpers;

/// <summary>
/// รอบ 217 (คำตัดสินข้อ 175 ข) — <b>ตัวตัดสินเดียว</b>ของ "คำขอสร้างซ้ำเชิงความหมาย" (ชั้นที่สองต่อจาก <see cref="IdempotencyKeyPolicy"/>):
/// เจ้าของรายงาน "กดจองเบิ้ลสองที บันทึกการจองซ้ำได้" (BK-2610-0003/0004 · บริการเดียวกัน · รอบเดียวกัน · ผู้จองคนเดียวกัน) —
/// ชั้นแรก (คีย์) กันได้เฉพาะหน้าที่ส่งคีย์ · ชั้นนี้กันหน้าเก่าที่แคชไว้ · สองแท็บ · แอป/ผู้เรียก API ที่ไม่ส่งคีย์
/// <list type="bullet">
///   <item>ผู้จองคนเดียวกัน = สมาชิก (CustomerId) ตรงกัน · หรือเบอร์ (ตัวเลขล้วน ≥ 9 หลัก · +66⇒0) / อีเมล (ตัวเล็ก) ตรงกัน —
///     ตัวทำให้เทียบได้คือ <see cref="CmsContactResolver.NormalizePhone"/>/<see cref="CmsContactResolver.NormalizeEmail"/> (ไม่มีสำเนา) ·
///     สมาชิกสองคนที่ CustomerId ต่างกัน ⇒ ไม่ใช่คนเดียวกันแม้เบอร์ตรง (ครอบครัวใช้เบอร์เดียว)</item>
///   <item>ภายใน <see cref="Window"/> นับจากเวลาสร้างใบเดิม · ใบที่ยกเลิก/ไม่มา ไม่นับ</item>
///   <item>ไม่มีตัวตนเลย (walk-in ที่พนักงานไม่กรอกเบอร์/อีเมล) ⇒ ไม่ตัดสิน (ไม่มีอะไรให้เทียบ — ห้ามเดา)</item>
///   <item>ตั้งใจจองเพิ่ม ⇒ ผู้เรียกส่ง <c>ConfirmAdditional</c> (หน้าเว็บถามยืนยันก่อน) · พ้นหน้าต่างแล้วจองซ้ำได้ตามปกติ (ความจุยังตรวจ)</item>
/// </list>
/// ผู้เรียกต้องตรวจ<b>ใต้ล็อกเดียวกับที่ตัดสินความจุ</b> (การจอง: ล็อกไซต์ · ที่พัก: ล็อกที่พัก) ⇒ คำขอพร้อมกันเห็นใบของกันและกัน
/// </summary>
public static class DuplicateSubmitGuard
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const string BookingRuleCode = "CMS-BOOKING-DUPLICATE";
    public const string LodgingRuleCode = "LODGING-DUPLICATE";

    public const string BookingGuestMessage =
        "คุณจองรอบนี้ไว้แล้วเมื่อสักครู่ (ระบบไม่บันทึกซ้ำ) — ดูรายละเอียดได้จากข้อความยืนยันการจอง หรือหน้า \"ตรวจสอบการจอง\" · "
        + "ถ้าต้องการจองเพิ่มอีกที่ในรอบเดียวกันจริง ให้กดยืนยันจองเพิ่ม";
    public const string LodgingGuestMessage =
        "มีการจองห้องช่วงวันนี้จากเบอร์โทร/อีเมลนี้เมื่อสักครู่แล้ว (ระบบไม่บันทึกซ้ำ) — ดูรายละเอียดจากลิงก์ในข้อความยืนยันการจอง · "
        + "ถ้าต้องการจองเพิ่มอีกชุดจริง ให้กดยืนยันจองเพิ่ม";

    public sealed record Requester(Guid? CustomerId, string? Phone, string? Email);

    public static DateTime WindowStart(DateTime nowUtc) => nowUtc - Window;

    /// <summary>ตัวตนที่เทียบได้ · null = ไม่มีอะไรให้เทียบ (ไม่ตัดสิน)</summary>
    public static Requester? IdentityOf(Guid? customerId, string? phone, string? email)
    {
        var p = CmsContactResolver.NormalizePhone(phone);
        var e = CmsContactResolver.NormalizeEmail(email);
        return customerId == null && p == null && e == null ? null : new Requester(customerId, p, e);
    }

    /// <summary>คนเดียวกันไหม — สมาชิกทั้งคู่ ⇒ ตัดสินด้วย id เท่านั้น · นอกนั้นเบอร์หรืออีเมลตรง</summary>
    private static bool SameRequester(Requester? a, Requester? b)
    {
        if (a == null || b == null) return false;
        if (a.CustomerId is Guid ac && b.CustomerId is Guid bc) return ac == bc;
        return (a.Phone != null && a.Phone == b.Phone) || (a.Email != null && a.Email == b.Email);
    }

    /// <summary>ใบแรก (ตามลำดับที่ผู้เรียกเรียงมา) ที่เป็นของผู้จองคนเดียวกัน — ผู้เรียกกรองบริษัท/ไซต์/รอบ/สถานะ/หน้าต่างเวลามาแล้ว</summary>
    public static T? FindDuplicate<T>(IEnumerable<T> candidates, Func<T, Requester?> identityOf, Requester? requester) where T : class
        => requester == null ? null : candidates.FirstOrDefault(c => SameRequester(identityOf(c), requester));

    /// <summary>เปิดใบเดิมให้ผู้ขอได้ไหม — พนักงาน · หรือสมาชิกที่เป็นเจ้าของใบเดิม (id จากโทเคนตรงกับใบ) · แขกนิรนาม ⇒ ไม่เปิด
    /// (เบอร์/อีเมลใครก็พิมพ์ได้ — ห้ามคืนโทเคนจัดการการจองของคนอื่น)</summary>
    public static bool Reveal(bool staff, Guid? requesterCustomerId, Guid? existingCustomerId)
        => staff || (requesterCustomerId is Guid r && existingCustomerId == r);
}
