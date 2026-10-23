using System.Linq;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **"สมุดรายวันใบไหนนับเข้ารายงาน" — กติกาตัวเดียว** (ทีม O 2026-10-22 · คำสั่งเจ้าของ)
///
/// <para><b>บั๊กที่มา (ทีม M พบ):</b> คำนวณ ภ.ง.ด.50 (<c>TaxService.GenerateCitReport</c> + ตัวเทียบความสด) · ภ.ง.ด.51 ·
/// FP&amp;A กรอง <c>Status == Posted</c> อย่างเดียว — แต่การกลับรายการ (<c>AccountingService.ReverseJournalEntryAsync</c>) เปลี่ยน
/// <b>ใบเดิม</b>เป็น <c>Reversed</c> และสร้าง<b>ใบกลับรายการ</b>เป็น <c>Posted</c> ⇒ ใบเดิมหายจากยอด แต่ใบกลับรายการยังถูกนับ ⇒
/// รายได้ 1,000 ที่ถูกยกเลิกกลายเป็น <b>−1,000</b> (ควรเป็น 0) ⇒ ฐาน ภ.ง.ด.50 ต่ำกว่าจริง = เสียภาษีขาด</para>
///
/// <para><b>กติกา (ตรงกับบัญชีแยกประเภท/งบทดลอง/งบกำไรขาดทุน <c>GetProfitAndLossAsync</c> ที่มีอยู่แล้ว):</b>
/// <list type="number">
/// <item><see cref="WhereInLedger(IQueryable{JournalEntryLine})"/> — <b>อยู่ในสมุด</b> = <c>Posted</c> หรือ <c>Reversed</c> · ใบเดิม (Reversed)
///   กับใบกลับรายการ (Posted) นับคู่กันจึงหักล้างกันเอง · ร่าง (Draft) / ยกเลิกก่อนลงบัญชี (Voided) ไม่นับ · ใช้กับยอดคงเหลือ ณ วันที่</item>
/// <item><see cref="WherePeriodActivity(IQueryable{JournalEntryLine})"/> — <b>ผลการดำเนินงานของงวด</b> = อยู่ในสมุด + <b>ไม่ใช่ใบปิดบัญชี</b>
///   (<c>IsClosingEntry</c> — C-T02 · ใบกลับรายการของใบปิดบัญชีก็ติดธงนี้) · ใช้กับรายได้/ค่าใช้จ่ายในช่วง (ภ.ง.ด.50/51 · FP&amp;A · งบรวม)</item>
/// </list></para>
///
/// <para><b>สิ่งที่ไม่ใช่กติกานี้ (ถูกต้องอยู่แล้ว ไม่ต้องเปลี่ยน):</b> query ที่ "ตัดทั้งคู่" (<c>Posted</c> + <c>ReversedByEntryId == null</c> +
/// <c>OriginalEntryId == null</c>) เช่นตัวกวาด JV ของ ภ.พ.30/ภ.ง.ด. — ผลเท่ากัน (คู่ที่หักล้างไม่มีผลกับยอด) · query ที่หา "JE ที่มีผลของเอกสารนี้"
/// เพื่อกลับรายการ/ผูกธนาคาร — ไม่ใช่การรวมยอดรายงาน</para>
///
/// <para>ล็อกจุดเรียก: <c>tools/required_call_site_check.py</c> (ห้ามกลับไปเขียน <c>JournalEntryStatus.Posted</c> เองในเมธอดที่ใช้ตัวนี้)</para>
/// </summary>
public static class ReportableJournal
{
    public const string RuleCode = "GL-REPORTABLE";

    /// <summary>สถานะที่ "อยู่ในสมุด" — <b>ตัวตั้งตัวเดียว</b> · ใช้ใน query ได้ตรง ๆ (<c>ReportableJournal.LedgerStatuses.Contains(j.Status)</c> →
    /// <c>IN (...)</c> · รูปแบบเดียวกับ <c>DocumentStatusRules.NotIssued</c>) เมื่อเงื่อนไขอยู่กลาง <c>Where</c> ที่แยกเป็น extension ไม่ได้</summary>
    public static readonly JournalEntryStatus[] LedgerStatuses = { JournalEntryStatus.Posted, JournalEntryStatus.Reversed };

    /// <summary>ใบนี้อยู่ในสมุด (นับเข้ายอด) ไหม — Posted หรือ Reversed (pure · ใช้ในหน่วยความจำ)</summary>
    public static bool InLedger(JournalEntryStatus status)
        => LedgerStatuses.Contains(status);

    /// <summary>หัวใบที่อยู่ในสมุด (Posted + Reversed) — แปลเป็น SQL ได้</summary>
    public static IQueryable<JournalEntry> WhereInLedger(this IQueryable<JournalEntry> q)
        => q.Where(j => LedgerStatuses.Contains(j.Status));

    /// <summary>บรรทัดของใบที่อยู่ในสมุด (Posted + Reversed) — แปลเป็น SQL ได้</summary>
    public static IQueryable<JournalEntryLine> WhereInLedger(this IQueryable<JournalEntryLine> q)
        => q.Where(l => LedgerStatuses.Contains(l.JournalEntry.Status));

    /// <summary>หัวใบที่นับเป็นผลการดำเนินงานของงวด (อยู่ในสมุด · ไม่ใช่ใบปิดบัญชี)</summary>
    public static IQueryable<JournalEntry> WherePeriodActivity(this IQueryable<JournalEntry> q)
        => q.WhereInLedger().Where(j => !j.IsClosingEntry);

    /// <summary>บรรทัดที่นับเป็นผลการดำเนินงานของงวด (อยู่ในสมุด · ไม่ใช่ใบปิดบัญชี)</summary>
    public static IQueryable<JournalEntryLine> WherePeriodActivity(this IQueryable<JournalEntryLine> q)
        => q.WhereInLedger().Where(l => !l.JournalEntry.IsClosingEntry);
}
