using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ใบปิดบัญชีสิ้นปีห้ามกลับรายการ/ยกเลิก/ลบ — ด่านตัวเดียว** (ฝ่ายค้านรอบส่งออก รอบ 5 P1 · erp-review/2026-10-14/EXPORT_AUDIT.md §"รอบ 5")
///
/// <para><b>บั๊กที่มา:</b> กลับรายการไม่ตรวจงวดของใบเดิม (หลักแก้ไขข้อผิดพลาดงวดก่อน — ตัวกลับลงงวดปัจจุบัน) และไม่รู้จักใบปิดปี ⇒ กลับใบปิดปีได้ ⇒ ตัวกลับ<b>ไม่ติดธง</b>
/// <c>IsClosingEntry</c> ⇒ งบกำไรขาดทุนของงวดที่ลงตัวกลับนับรายได้/ค่าใช้จ่ายทั้งปีกลับด้าน · <c>YearEndClosing</c> ยังชี้ใบที่ถูกกลับ ·
/// งวดของปีที่ปิดยังล็อก ⇒ ปีนั้น "ปิดแล้ว" แต่กำไรไม่ถูกโอน · ลบแบบหลายใบไม่ตรวจงวดเลย ⇒ ลบใบปิดปีทิ้งได้</para>
///
/// <para><b>กติกา:</b> ใบที่ติดธง <c>IsClosingEntry</c> (ใบปิดปี + ใบปิดรายเดือนรุ่นเก่า CL-) หรือถูกอ้างโดย <c>YearEndClosing.ClosingJournalEntryId</c>
/// ⇒ กลับรายการ (ทุกทาง รวมทางระบบ · ผ่านสมุดรายวัน · ยกเลิก · ยกเลิกหลายใบ · แก้ไขด้วยการกลับรายการ · API ภายนอก) และลบ (ใบเดียว/หลายใบ) <b>ล้มดัง</b>
/// พร้อมทางไปต่อ — เปิดปีที่ปิด = ปุ่ม "ยกเลิกการปิดปี" (เจ้าของบริษัท · <see cref="YearEndReopenRule"/> — กลับใบปิดปีด้วยทางเฉพาะของมันเอง ไม่ผ่านทางเหล่านี้) ·
/// ยื่นแล้ว/แค่พบข้อผิดพลาด = ใบปรับปรุงกำไรสะสม (แก้ไขข้อผิดพลาดงวดก่อน — TFRS for NPAEs บทที่ 5) ในงวดที่ยังเปิด ·
/// ใบปิดปีที่ถูกยกเลิกแล้วและใบกลับรายการของมันติดธง <c>IsClosingEntry</c> ทั้งคู่ ⇒ ด่านนี้คุมต่อ (คำตัดสินข้อ 150: ความถูกต้องของสมุดบัญชี = บล็อก)</para>
/// </summary>
public static class ClosingEntryLock
{
    public const string RuleCode = "GL-YEAR-END-CLOSING-ENTRY";
    public const string LegalReference = "พ.ร.บ.การบัญชี 2543 ม.11 (ปิดบัญชีทุกรอบสิบสองเดือน) · TFRS for NPAEs บทที่ 5 (การแก้ไขข้อผิดพลาด)";

    /// <summary>ข้อความของด่าน (pure) · <paramref name="action"/> = สิ่งที่ผู้ใช้พยายามทำ ("กลับรายการ" · "ยกเลิก" · "ลบ")</summary>
    public static string BlockMessage(string action, IEnumerable<(string EntryNumber, int? FiscalYear)> entries)
        => $"{action}ใบปิดบัญชีไม่ได้: "
            + string.Join(" · ", entries.Select(e => e.FiscalYear is int y
                ? $"{e.EntryNumber} (ใบปิดบัญชีสิ้นปี {y + 543})"
                : $"{e.EntryNumber} (ใบปิดบัญชี)"))
            + $" — ใบนี้โอนกำไร(ขาดทุน)เข้ากำไรสะสมและผูกกับการปิดปี (งวดของปีนั้นถูกล็อก) · {action}แล้วงบกำไรขาดทุนของงวดที่ลงรายการจะนับรายได้/ค่าใช้จ่ายทั้งปีกลับด้าน "
            + "และปีนั้นยัง \"ปิดแล้ว\" ทั้งที่กำไรไม่ถูกโอน (ใบที่การยกเลิกการปิดปีกลับรายการไว้แล้วก็ห้ามแตะเช่นกัน) · ยังไม่มีอะไรถูกบันทึก · "
            + YearEndReopenRule.WayForward + " (พ.ร.บ.การบัญชี 2543 ม.11)";

    /// <summary>
    /// ล้มดังเมื่อมีใบใดใน <paramref name="entryIds"/> เป็นใบปิดบัญชี (ธง <c>IsClosingEntry</c> หรือถูกอ้างโดย <c>YearEndClosing</c>) ของบริษัท <paramref name="companyId"/> ·
    /// เรียก<b>ก่อน</b>เขียนอะไร (ชุดหลายใบตรวจทั้งชุดก่อนเริ่ม — ไม่ทำครึ่งชุด)
    /// </summary>
    public static async Task EnsureNotClosingEntriesAsync(this AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> entryIds,
        string action, CancellationToken ct = default)
    {
        if (entryIds.Count == 0) return;
        var ids = entryIds.Distinct().ToList();
        var closingRefs = await db.YearEndClosings.AsNoTracking()
            .Where(y => y.CompanyId == companyId && ids.Contains(y.ClosingJournalEntryId))
            .Select(y => new { y.ClosingJournalEntryId, y.FiscalYear })
            .ToListAsync(ct);
        var referenced = closingRefs.Select(r => r.ClosingJournalEntryId).ToList();
        var hits = await db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.CompanyId == companyId && ids.Contains(j.Id) && (j.IsClosingEntry || referenced.Contains(j.Id)))
            .Select(j => new { j.Id, j.EntryNumber })
            .ToListAsync(ct);
        if (hits.Count == 0) return;
        throw new BusinessRuleException(BlockMessage(action, hits
            .OrderBy(h => h.EntryNumber, StringComparer.Ordinal)
            .Select(h => (h.EntryNumber, closingRefs.Where(r => r.ClosingJournalEntryId == h.Id).Select(r => (int?)r.FiscalYear).FirstOrDefault()))),
            RuleCode);
    }
}
