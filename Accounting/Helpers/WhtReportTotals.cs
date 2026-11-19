using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตั้งตัวเดียวของ "บรรทัดไหนนับเข้ายอดแบบ ภ.ง.ด." และผลรวมของมัน** —
/// ยอดหัวรายงาน (generate · กดติ๊ก/บันทึก · RecalcWhtTotals) · ตัวตรวจก่อนยื่น
/// (<c>TaxComplianceChecker</c> RD-WHT-002 / RD-PND1-002) · ไฟล์ e-Filing/ใบแนบ (<c>PndDetailLines</c>)
/// ต้องเรียกตัวนี้ทั้งหมด ห้ามเขียนตัวกรองเอง (กฎเหล็ก #4 F2 ข้อ 4)
///
/// <para>ที่มา (ผู้ใช้รายงาน 2026-11-19 · ภ.ง.ด.53 ต.ค. 2569 ร่าง): ยอดหัว 178.65 แต่กด "ยื่นภาษี" แล้วตัวตรวจฟ้อง
/// <c>RD-WHT-002 — ผลรวมในรายการย่อย (357.30) ไม่เท่ากับ TotalTaxWithheld (178.65)</c> — สองเท่าพอดี.
/// ต้นเหตุ: ยอดหัวนับเฉพาะ "แถวที่ยื่นจริง" (<c>!IsExcluded && IncomeTypeCode != "SUMMARY"</c>) แต่ตัวตรวจบวก
/// <c>r.Lines.Sum(TaxAmount)</c> <b>ทุกแถว</b> ⇒ แถว <c>[สรุป] ผู้ถูกหัก</c> (ยอดรวมของแถวรายการของรายเดียวกันซึ่งเกิดเมื่อ
/// 50 ทวิ ใบเดียวมีหลายประเภทเงินได้ หรือรายเดียวได้หลายใบ) ถูกนับซ้ำกับแถวรายการ ⇒ ×2 · แถวที่ติ๊กออก
/// (50 ทวิ ร่าง · เอกสารที่ยังไม่ออกหนังสือรับรอง) ก็ถูกบวกด้วย. ข้อมูลที่เก็บไว้ถูกอยู่แล้ว (แถว [สรุป] เป็นแถวแสดงผล) —
/// ตัวตรวจคือตัวที่ผิด</para>
/// </summary>
public static class WhtReportTotals
{
    /// <summary>แถว [สรุป] ต่อผู้ถูกหัก — แถวแสดงผล ไม่ใช่รายการที่ยื่น</summary>
    public static bool IsSummary(TaxReportLine l)
        => string.Equals((l.IncomeTypeCode ?? "").Trim(), VatReportLineKind.Summary, StringComparison.OrdinalIgnoreCase);

    /// <summary>บรรทัดนี้นับเข้ายอดแบบ/ไฟล์ยื่นไหม — ไม่ใช่แถว [สรุป] และผู้ทำบัญชีไม่ได้ติ๊กออก</summary>
    private static bool IsCounted(TaxReportLine l) => !l.IsExcluded && !IsSummary(l);

    /// <summary>บรรทัดที่นับเข้ายอด (ลำดับเดิมของ <paramref name="lines"/>)</summary>
    public static IEnumerable<TaxReportLine> CountedLines(IEnumerable<TaxReportLine> lines) => lines.Where(IsCounted);

    /// <summary>ผลรวมเงินได้/ภาษีของบรรทัดที่นับเข้ายอด — สูตรเดียวของยอดหัวและตัวตรวจก่อนยื่น</summary>
    public static (decimal Income, decimal Tax) Of(IEnumerable<TaxReportLine> lines)
    {
        decimal income = 0m, tax = 0m;
        foreach (var l in lines)
        {
            if (!IsCounted(l)) continue;
            income += l.IncomeAmount;
            tax += l.TaxAmount;
        }
        return (income, tax);
    }

    /// <summary>เขียนยอดหัว <c>TotalIncome</c>/<c>TotalTaxWithheld</c> จากบรรทัด — ทางเดียวที่ยอดหัวของ ภ.ง.ด. ถูกตั้ง</summary>
    public static void Apply(TaxReport report)
    {
        var (income, tax) = Of(report.Lines);
        report.TotalIncome = income;
        report.TotalTaxWithheld = tax;
    }
}
