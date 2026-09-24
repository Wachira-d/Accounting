using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **ตัว "ขึ้นเดือนใหม่" ตัวเดียวของตัวนับโควตารายเดือนทุกตัว** — pure · ไม่รับ DbContext
///
/// ═══ ที่มา (เจ้าของรายงาน 2026-09-24) ═══
/// "โควต้า OCR ดูเหมือนจะไม่ได้ reset รายเดือน ตอนนี้ scan สะสมมา 3 เดือนยอดเพิ่มขึ้นเรื่อย ๆ
/// จนเต็ม 200 แสกนต่อไม่ได้แล้ว"
///
/// ต้นเหตุ: ตัวนับรายเดือน 5 ตัวบน <see cref="Subscription"/> ใช้ **วันรีเซ็ตตัวเดียวกัน**
/// (<c>UsageResetDate</c>) แต่มี 4 จุดที่ "ขึ้นเดือนใหม่" และแต่ละจุดล้างตัวนับ**คนละชุด**:
/// <code>
///   จุด                                   เอกสาร  JE  OCR รวม  OCR Azure  OCR local
///   SubscriptionService (ตรวจสิทธิ์)         ✓     ✓      ✗        ✗          ✗
///   OcrQuotaService.TryConsumeAsync         ✗     ✗      ✓        ✗          ✗
///   OcrQuotaService.TryConsumeForEngine     ✗     ✗      ✓        ✓          ✓   ← ไม่มีใครเรียก
///   OcrQuotaService.ResetMonthlyUsage (job) ✓     ✓      ✓        ✗          ✗
/// </code>
/// จุดไหนรันก่อนก็เลื่อน <c>UsageResetDate</c> ไปเดือนหน้า ⇒ จุดที่เหลือเห็นวันรีเซ็ตเป็น
/// "อนาคต" แล้ว**ไม่ล้างตัวนับของตัวเองอีกเลย** · ตัวตรวจสิทธิ์รันแทบทุกครั้งที่ผู้ใช้ทำอะไร
/// ⇒ ชนะเกือบทุกเดือน ⇒ **ตัวนับ OCR ไม่เคยถูกล้าง สะสมข้ามเดือนจนเต็ม** · ส่วนตัวนับ Azure/local
/// ไม่มีจุดที่รันจริงล้างให้เลยตั้งแต่แรก
///
/// ⇒ กติกาเดียว: **ใครเลื่อนวันรีเซ็ต ต้องล้างตัวนับทุกตัวที่วันนั้นคุม** — จุดที่ขึ้นเดือนใหม่
/// ทุกจุดต้องเรียก <see cref="RollIfDue"/> ห้ามเขียนการล้างเองอีก (F2 ข้อ 4)
/// </summary>
public static class SubscriptionUsageRollover
{
    /// <summary>วันรีเซ็ตถัดไป = วันที่ 1 ของเดือนถัดไป (เวลา UTC — ตรงกับที่ทุกจุดเดิมใช้)</summary>
    public static DateTime NextResetDate(DateTime utcNow)
        => new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);

    /// <summary>ตัวนับชุดนี้เป็น "ของเดือนก่อน" แล้วหรือยัง</summary>
    public static bool IsStale(DateTime usageResetDate, DateTime utcNow) => utcNow >= usageResetDate;

    /// <summary>ค่าที่ควรใช้ตัดสินโควตา **ตอนนี้** — ตัวนับที่ค้างจากเดือนก่อน (ยังไม่มีใครเขียนเดือนนี้)
    /// นับเป็น 0 · ใช้กับทุกเส้นที่ **อ่าน** ตัวนับโดยไม่ได้ล็อกแถวเพื่อเขียน (หน้าแสดงผล ·
    /// ด่านก่อนสแกน · ผลรวมของหลายบริษัทใต้ License เดียว ที่บริษัทพี่น้องอาจยังไม่ขึ้นเดือนใหม่)</summary>
    public static int Effective(int counter, DateTime usageResetDate, DateTime utcNow)
        => IsStale(usageResetDate, utcNow) ? 0 : counter;

    /// <summary>ถ้าถึงวันรีเซ็ตแล้ว: ล้าง**ตัวนับรายเดือนทุกตัว**แล้วเลื่อนวันรีเซ็ต · คืน true เมื่อขึ้นเดือนใหม่จริง
    /// <para>ห้ามล้างแค่บางตัว — นั่นคือบั๊กที่ไฟล์นี้ถูกสร้างขึ้นมาแก้</para></summary>
    public static bool RollIfDue(Subscription sub, DateTime utcNow)
    {
        if (!IsStale(sub.UsageResetDate, utcNow)) return false;
        sub.CurrentMonthDocuments = 0;
        sub.CurrentMonthJournalEntries = 0;
        sub.CurrentMonthOcrPages = 0;
        sub.CurrentMonthAzureOcrPages = 0;
        sub.CurrentMonthLocalOcrPages = 0;
        sub.UsageResetDate = NextResetDate(utcNow);
        return true;
    }
}
