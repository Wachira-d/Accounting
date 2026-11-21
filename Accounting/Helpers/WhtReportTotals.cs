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

    /// <summary>รหัสแถว "ผลต่างภาษี" ของ ภ.ง.ด.91 (ภาษีคำนวณ − ภาษีหัก ณ ที่จ่ายทั้งปีของพนักงาน) — แถวแสดงผล ไม่ใช่ภาษีที่หักนำส่ง
    /// (ยอดหัว ภ.ง.ด.91 "ภาษีหักนำส่งแล้ว (ทั้งปี)" = Σ ภาษีที่หักจริงเท่านั้น · รอบ 273 ฝ่ายค้านข้อ 4: เดิมกดบันทึกบรรทัดแล้วยอดหัวถูกบวกผลต่างเข้าไป
    /// กลายเป็น "ภาษีคำนวณ" ทั้งที่ตอนสร้างเป็นยอดหักจริง — ความหมายเปลี่ยนตามว่าเคยกดบันทึกหรือไม่)</summary>
    public const string Pnd91TaxAdditional = "TAX_ADDITIONAL";
    public const string Pnd91TaxRefund = "TAX_REFUND";

    private static bool IsPnd91Difference(TaxReportLine l)
    {
        var c = (l.IncomeTypeCode ?? "").Trim();
        return string.Equals(c, Pnd91TaxAdditional, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c, Pnd91TaxRefund, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>บรรทัดนี้นับเข้ายอดแบบ/ไฟล์ยื่นไหม — ไม่ใช่แถว [สรุป] · ไม่ใช่แถวผลต่าง ภ.ง.ด.91 · ผู้ทำบัญชีไม่ได้ติ๊กออก</summary>
    private static bool IsCounted(TaxReportLine l) => !l.IsExcluded && !IsSummary(l) && !IsPnd91Difference(l);

    /// <summary>บรรทัดที่นับเข้ายอด (ลำดับเดิมของ <paramref name="lines"/>)</summary>
    public static IEnumerable<TaxReportLine> CountedLines(IEnumerable<TaxReportLine> lines) => lines.Where(IsCounted);

    /// <summary>ผลรวมเงินได้/ภาษีของบรรทัดที่นับเข้ายอด — สูตรเดียวของยอดหัว ตัวตรวจก่อนยื่น ท้ายตารางบนจอ และแถว [สรุป]</summary>
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

    /// <summary>ยอดของแถว [สรุป] ผู้ถูกหักรายหนึ่ง = Σ แถวที่นับเข้ายอดของเลขผู้เสียภาษีนั้น (ตัวตั้งเดียวทั้งตอนสร้างและตอนติ๊ก/แก้บรรทัด)</summary>
    private static (decimal Income, decimal Tax) PayeeSummaryOf(IEnumerable<TaxReportLine> lines, TaxReportLine summary)
    {
        var key = (summary.TaxPayerId ?? "").Trim();
        // รอบ 275 แพ็ก B (T2 L2): คีย์ = เลขผู้เสียภาษี + สาขา (เดิมเลขอย่างเดียว ⇒ ต่างสาขารวมกัน) · แถวสรุปรุ่นเก่าไม่มีสาขา ⇒ เทียบเลขอย่างเดียวเหมือนเดิม
        var branch = summary.TaxPayerBranchCode == null ? null : SummaryBranchKey(summary.TaxPayerBranchCode);
        return Of(lines.Where(l => !IsSummary(l) && (l.TaxPayerId ?? "").Trim() == key
            && (branch == null || SummaryBranchKey(l.TaxPayerBranchCode) == branch)));
    }

    /// <summary>คีย์สาขาของแถว [สรุป] (ว่าง/00000 = สำนักงานใหญ่) — ตัวเดียวของตอนสร้างแถวสรุปและตอนคำนวณยอด</summary>
    public static string SummaryBranchKey(string? branch) => TaxReportDraftFreshness.NormalizeBranch(branch);

    /// <summary>รอบ 284 (T2 L2 ส่วนที่ค้างจาก 275-B): แถว [สรุป] ต้องตรงกับบรรทัดที่นับอยู่ตอนนี้ — ผู้ถูกหัก (เลข + สาขา) ที่มี ≥ 2 บรรทัดที่นับต้องมีแถวสรุปหนึ่งแถว ·
    /// แถวสรุปที่รายนั้นเหลือ &lt; 2 บรรทัด (ติ๊กออก) หรือซ้ำ ⇒ ลบ · ตัวเดียวของตอนสร้างรายงานและตอนติ๊ก/แก้บรรทัด · แสดงผลอย่างเดียว (ยอดหัวไม่นับแถวสรุป) ·
    /// แถวสรุปรุ่นเก่าที่ไม่มีสาขาจับคู่ด้วยเลขอย่างเดียว (เหมือน <see cref="PayeeSummaryOf"/>)</summary>
    public static (List<TaxReportLine> Add, List<TaxReportLine> Remove) SyncSummaryRows(TaxReport report)
    {
        var groups = report.Lines.Where(l => IsCounted(l) && !string.IsNullOrWhiteSpace(l.TaxPayerId))
            .GroupBy(l => (Id: l.TaxPayerId!.Trim(), Branch: SummaryBranchKey(l.TaxPayerBranchCode)))
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.ToList());
        var covered = new HashSet<(string Id, string Branch)>();
        var remove = new List<TaxReportLine>();
        foreach (var s in report.Lines.Where(IsSummary).OrderBy(l => l.LineOrder).ToList())
        {
            var id = (s.TaxPayerId ?? "").Trim();
            var keys = groups.Keys.Where(k => k.Id == id
                && (s.TaxPayerBranchCode == null || k.Branch == SummaryBranchKey(s.TaxPayerBranchCode))).ToList();
            if (keys.Count == 0 || keys.All(covered.Contains)) { remove.Add(s); continue; }
            foreach (var k in keys) covered.Add(k);
        }
        var order = report.Lines.Count == 0 ? 0 : report.Lines.Max(l => l.LineOrder);
        var add = groups.Where(g => !covered.Contains(g.Key)).OrderBy(g => g.Key.Id).ThenBy(g => g.Key.Branch)
            .Select(g => new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = ++order,
                TaxPayerId = g.Key.Id,
                TaxPayerName = g.Value[0].TaxPayerName,
                TaxPayerBranchCode = g.Key.Branch,
                Description = $"[สรุป] {g.Value[0].TaxPayerName}",
                TaxRate = 0,
                IncomeTypeCode = VatReportLineKind.Summary,
            }).ToList();
        return (add, remove);
    }

    /// <summary>เขียนยอดหัว <c>TotalIncome</c>/<c>TotalTaxWithheld</c> จากบรรทัด + คำนวณแถว [สรุป] ต่อผู้ถูกหักใหม่
    /// (รอบ 273 ฝ่ายค้านข้อ 3: ติ๊กออก/แก้ยอดบรรทัดแล้วแถวสรุปค้างค่าเดิม) — ทางที่ยอดหัวของรายงานที่คิดจากบรรทัดถูกตั้ง
    /// (ภ.ง.ด.1/2/3/53/54 · ภ.ง.ด.91 · ภ.พ.36 ใช้ <see cref="CountedLines"/> ลงช่องของตัวเอง · ภ.พ.30/ภ.ง.ด.50 มีสูตรของตัวเอง)</summary>
    public static void Apply(TaxReport report)
    {
        foreach (var s in report.Lines.Where(IsSummary).ToList())
        {
            var (si, st) = PayeeSummaryOf(report.Lines, s);
            s.IncomeAmount = si;
            s.TaxAmount = st;
        }
        var (income, tax) = Of(report.Lines);
        report.TotalIncome = income;
        report.TotalTaxWithheld = tax;
    }
}
