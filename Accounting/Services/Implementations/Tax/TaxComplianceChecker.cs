using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Tax;

/// <summary>
/// Local rule-based pre-checker for Thai tax filings (ภพ.30, ภงด.3,
/// ภงด.53). Runs deterministic compliance rules against a draft
/// TaxReport BEFORE the user submits to the RD — catches mechanical
/// errors that would otherwise come back as e-Filing rejections.
///
/// Why local rules instead of pure AI:
///   • Rules are well-defined in กรมสรรพากร publications — RFC-style
///     enforceable. No reason to spend DeepSeek tokens on questions
///     with deterministic answers.
///   • Free + instant. Pre-check is run on every "preview" click;
///     paying $0.001 each round-trip would add up.
///   • AI's role (AiFeatureKey.TaxFilingPreCheck) becomes the
///     NARRATIVE layer on top: "here's what's wrong + here's how to
///     fix it in Thai." Hybrid is strictly better than either alone.
///
/// Severity:
///   • Error    — blocks submission. Mechanical rejection by RD.
///   • Warning  — submit possible but likely incorrect (worth checking).
///   • Info     — observation; no action required.
/// </summary>
public interface ITaxComplianceChecker
{
    /// <summary>ตรวจรายงานภาษีของ <paramref name="companyId"/> — รายงานของบริษัทอื่นตอบ "ไม่พบ"
    /// (ผลตรวจ B-03 · รอบ 193: เดิมไม่มี companyId ⇒ อ่านยอด VAT/WHT ข้ามบริษัทได้ด้วย reportId)</summary>
    Task<TaxComplianceReport> CheckAsync(Guid companyId, Guid taxReportId, CancellationToken ct = default);
}

public sealed record TaxComplianceFinding(
    string Code,                   // "RD-WHT-001" etc.
    string Severity,               // Error | Warning | Info
    string Message,
    string? FixHint);

public sealed record TaxComplianceReport(
    Guid TaxReportId,
    TaxType TaxType,
    int Year, int Month,
    bool CanSubmit,                // false when any Error
    int ErrorCount, int WarningCount,
    IReadOnlyList<TaxComplianceFinding> Findings);

public class TaxComplianceChecker : ITaxComplianceChecker
{
    private readonly AccountingDbContext _db;

    public TaxComplianceChecker(AccountingDbContext db) { _db = db; }

    public async Task<TaxComplianceReport> CheckAsync(Guid companyId, Guid taxReportId, CancellationToken ct = default)
    {
        // กฎ M: กรอง CompanyId ที่แหล่ง (global query filter กรองแค่ IsDeleted) — ผ่านตัวกรองกลาง
        // Helpers/TaxReportTenantScope ที่มีเทสต์ทั้งสองทิศ
        var report = await _db.TaxReports.AsNoTracking()
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(Accounting.Helpers.TaxReportTenantScope.ById(companyId, taxReportId), ct);
        if (report == null)
            throw new KeyNotFoundException("ไม่พบรายงานภาษีนี้ในบริษัทที่เลือก");

        var findings = new List<TaxComplianceFinding>();

        // ── Common cross-form checks ─────────────────────────────────
        if (report.Year < 2018 || report.Year > DateTime.UtcNow.Year + 1)
            findings.Add(new("RD-GEN-001", "Error",
                $"ปี {report.Year} อยู่นอกช่วงที่ระบบรองรับ (2018-{DateTime.UtcNow.Year + 1}).",
                "เลือกปีให้อยู่ในช่วงที่ถูกต้อง."));
        if (report.Month < 1 || report.Month > 12)
            findings.Add(new("RD-GEN-002", "Error",
                $"เดือน {report.Month} ไม่ถูกต้อง.", null));
        if (report.FilingLockedAt.HasValue)
            findings.Add(new("RD-GEN-003", "Info",
                $"รายงานถูกล็อกการยื่นแล้วเมื่อ {report.FilingLockedAt:yyyy-MM-dd}.", null));

        // Deadline check — gentle warning, not error, in case admin is
        // back-filling late.
        // รอบ 201 ทีม PL (ฝ่ายค้าน PL-B1): วันหยุดราชการของแพลตฟอร์ม — ตัวตัดสินเดียวกับปฏิทินภาษี/หน้านำส่ง (ตารางว่าง = เสาร์/อาทิตย์ เดิม)
        var holidays = report.Year is >= 2018 and <= 2100
            ? await Accounting.Services.Implementations.PlatformHolidayStore.LoadSetAsync(_db, report.Year, report.Year, ct: ct)
            : null;
        var deadline = DeadlineFor(report.TaxType, report.Year, report.Month, holidays);
        if (deadline.HasValue && DateTime.UtcNow.Date > deadline.Value)
        {
            // รอบ 201 B-7: แบบที่ยังไม่ยืนยันมาตรการ e-Filing (ภ.พ.36/ภ.ง.ด.54) เตือนตามวันกระดาษ — บอกวัน e-Filing ไว้ด้วยให้ผู้ใช้ตัดสินได้
            var eFiling = Accounting.Helpers.TaxFilingDeadline.EFilingFor(report.TaxType, report.Year, report.Month, holidays);
            var caveat = eFiling.HasValue && eFiling.Value > deadline.Value
                ? $" (กำหนดแบบกระดาษ · ถ้ามาตรการขยาย e-Filing ครอบแบบนี้ = {eFiling:yyyy-MM-dd} — ยังไม่ยืนยันตัวบท)"
                : "";
            findings.Add(new("RD-DEADLINE", "Warning",
                $"พ้นกำหนดยื่นแล้ว ({deadline:yyyy-MM-dd}){caveat}. อาจมีเบี้ยปรับ.",
                "เตรียมเงินค่าปรับ + คำชี้แจงเหตุล่าช้า ถ้ามี."));
        }

        // ── Per-form checks ──────────────────────────────────────────
        switch (report.TaxType)
        {
            case TaxType.VAT: CheckVatPp30(report, findings); break;
            case TaxType.WithholdingTax2:   // รอบ 204 — ภ.ง.ด.2 ใช้ invariant เดียวกัน (ยอดหัว = Σ บรรทัด · เลขผู้เสียภาษีครบ)
            case TaxType.WithholdingTax3:
            case TaxType.WithholdingTax53: CheckPnd3_53(report, findings); break;
            // รอบ 275 แพ็ก B (T2 M2): ภ.ง.ด.54 invariant เดียวกัน (ยอดหัว = Σ บรรทัด) — ยกเว้นเลขผู้เสียภาษีไทย (ผู้รับต่างประเทศ · L4)
            case TaxType.WithholdingTax54: CheckPnd3_53(report, findings, foreignPayees: true); break;
            case TaxType.VatPp36: CheckPp36(report, findings); break;
            case TaxType.WithholdingTax1: CheckPnd1(report, findings); break;
        }

        var errs = findings.Count(f => f.Severity == "Error");
        var warns = findings.Count(f => f.Severity == "Warning");
        return new TaxComplianceReport(
            report.Id, report.TaxType, report.Year, report.Month,
            CanSubmit: errs == 0,
            ErrorCount: errs, WarningCount: warns,
            Findings: findings);
    }

    private static void CheckVatPp30(TaxReport r, List<TaxComplianceFinding> f)
    {
        // Arithmetic invariant — Output - Input == Net (or refund).
        var computed = r.OutputVat - r.InputVat;
        if (Math.Abs(computed - r.NetVat) > 0.50m)
            f.Add(new("RD-VAT-001", "Error",
                $"NetVat ไม่ตรงกับ Output ({r.OutputVat:N2}) - Input ({r.InputVat:N2}) = {computed:N2}; ระบุไว้ {r.NetVat:N2}.",
                "กดคำนวณใหม่ก่อนยื่น."));
        if (r.OutputVat < 0 || r.InputVat < 0)
            f.Add(new("RD-VAT-002", "Error", "OutputVat / InputVat ติดลบไม่ได้.", null));
        // 7% rate check — if input/output exists, expect roughly
        // 7% of base (loose; partial-VAT lines exist). We can only
        // check the aggregate; tight per-invoice checks live in
        // RdComplianceValidator.
        var sumSales = r.Lines.Where(l => l.Description?.Contains("ขาย") == true).Sum(l => l.TaxAmount);
        if (sumSales > 0 && Math.Abs(sumSales - r.OutputVat) > 5m)
            f.Add(new("RD-VAT-003", "Warning",
                $"ผลรวมภาษีขายในรายการย่อย ({sumSales:N2}) ไม่เท่ากับ OutputVat ({r.OutputVat:N2}).",
                "ตรวจสอบว่าครอบคลุมทุกใบกำกับภาษีขายของเดือนนี้."));
    }

    /// <summary>ผลต่างที่ยอมให้ระหว่างยอดหัวกับ Σ บรรทัด — รอบ 275 แพ็ก B (T2 L1): ทุกทางเขียนยอดหัวผ่านตัวตั้งเดียว (WhtReportTotals.Apply) แล้ว
    /// ⇒ ผลต่างที่ถูกต้องคือ 0.00 (เดิม 0.50 — 0.49 บาทที่ผ่านได้คือสัญญาณว่ามีทางเขียนยอดหัวที่ไม่ผ่านตัวตั้ง)</summary>
    internal const decimal HeaderTolerance = 0m;

    /// <summary>รอบ 275 แพ็ก B (ฝ่ายค้าน MED-3): Error ที่ <b>ทางยื่นฝั่งเซิร์ฟเวอร์บล็อก</b> (<c>TaxService.EnsureReportFileableAsync</c>) — เฉพาะกติกาที่ผลตรวจ T2 M1 ระบุ
    /// (ยอดหัว ≠ Σ บรรทัด ของ ภ.ง.ด./ภ.พ.36 · บรรทัดยื่นไม่มีเลขผู้เสียภาษี = สรรพากรปฏิเสธไฟล์) · Error อื่น (ปี/เดือนผิด ซึ่งสร้างไม่ได้อยู่แล้ว · ยอด ภ.พ.30 ติดลบ
    /// ในเดือนที่ใบลดหนี้มากกว่ายอดขาย · NetVat ของ ภ.พ.30 ที่มีเครดิตยกมา) คงเป็นข้อความบนหน้าตรวจก่อนยื่น ไม่บล็อกที่เซิร์ฟเวอร์</summary>
    public static readonly IReadOnlySet<string> ServerBlockingCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "RD-WHT-002", "RD-WHT-005", "RD-PND1-002", "RD-PP36-001",
    };

    private static void CheckPnd3_53(TaxReport r, List<TaxComplianceFinding> f, bool foreignPayees = false)
    {
        // PND.3 = individual recipients; PND.53 = juristic recipients.
        // The forms differ by recipient type but share the structural
        // invariant: tax withheld = sum of line withholdings.
        if (r.TotalTaxWithheld <= 0 && r.TotalIncome <= 0)
            f.Add(new("RD-WHT-001", "Warning",
                "ทั้ง TotalIncome และ TotalTaxWithheld = 0 — รายงานเปล่า.",
                "ถ้าไม่มียอด withhold ในเดือนนี้ ไม่จำเป็นต้องยื่น."));

        // รอบ 273 (ผู้ใช้รายงาน ภ.ง.ด.53 ต.ค. 2569: 357.30 vs 178.65 = ×2): เดิม r.Lines.Sum ทุกแถว ⇒ แถว [สรุป] ต่อผู้ถูกหัก
        // + แถวที่ติ๊กออก ถูกบวกซ้ำ · ตอนนี้ใช้ตัวตั้งเดียวกับยอดหัว (Helpers/WhtReportTotals) — ด่านยังจับยอดหัวที่ค้าง/ถูกแก้มือ
        var (_, sumWht) = Accounting.Helpers.WhtReportTotals.Of(r.Lines);
        // รอบ 273 ฝ่ายค้านข้อ 2: ฟ้องไม่ว่า Σ จะเป็นเท่าไร — เดิม `sumWht > 0 &&` ⇒ ติ๊กออกครบทุกแถวแต่ยอดหัวค้างค่าเก่า = ผ่านเงียบ
        if (Math.Abs(sumWht - r.TotalTaxWithheld) > HeaderTolerance)
            f.Add(new("RD-WHT-002", "Error",
                $"ผลรวมภาษีหัก ณ ที่จ่ายในรายการย่อย ({sumWht:N2}) ไม่เท่ากับ TotalTaxWithheld ({r.TotalTaxWithheld:N2}).",
                "กด \"บันทึก\" ที่รายงาน (ระบบคำนวณยอดหัวจากบรรทัดใหม่) หรือกด \"สร้างใหม่\"."));

        // Effective rate sanity — typical PND.3/53 codes range 1-15%.
        // Anything outside that band is almost certainly miskeyed.
        if (r.TotalIncome > 0)
        {
            var rate = r.TotalTaxWithheld / r.TotalIncome;
            if (rate > 0.20m)
                f.Add(new("RD-WHT-003", "Warning",
                    $"อัตราภาษีหักรวมสูงผิดปกติ ({rate:P1}) — ปกติไม่เกิน 15%.",
                    "ตรวจสอบรหัสประเภทเงินได้ (50, 50ทวิ, 53) แต่ละบรรทัด."));
            if (rate > 0 && rate < 0.005m)
                f.Add(new("RD-WHT-004", "Warning",
                    $"อัตราภาษีหักรวมต่ำผิดปกติ ({rate:P2}) — ตรวจสอบว่าหัก ณ ที่จ่ายครบ.",
                    null));
        }

        // PND.3 = individuals → expect citizen IDs (13 digits, no juristic prefixes).
        // PND.53 = juristic → expect juristic IDs (also 13 but different ranges).
        // Per-line tax-ID format checks live in RdComplianceValidator;
        // here we just count "no tax ID" lines.
        // เฉพาะแถวที่ยื่นจริง — แถวติ๊กออก/แถว [สรุป] ไม่ลงไฟล์ยื่น (ตัวกรองเดียวกับ PndDetailLines)
        // รอบ 275 แพ็ก B (T2 L4): ภ.ง.ด.54 ผู้รับอยู่ต่างประเทศ — ไม่มีเลขผู้เสียภาษีไทยโดยธรรมชาติ (ตัวเดียวกับ WhtCertIssuePlan ที่ไม่บังคับเลขของผู้รับ ตปท.)
        //   ถ้าฟ้องข้อนี้ = ฟ้องใบถูกทุกใบ (F2 ข้อ 8)
        var missingTaxId = foreignPayees ? 0
            : Accounting.Helpers.WhtReportTotals.CountedLines(r.Lines).Count(l => string.IsNullOrWhiteSpace(l.TaxPayerId));
        if (missingTaxId > 0)
            // รอบ 275 แพ็ก B (T2 M4): เดิมบอก "เปิดบรรทัดและเติมเลข" — บรรทัดแก้เลขไม่ได้ (รับแค่ ยอด/อัตรา/คำอธิบาย/ติ๊ก) ⇒ ทางแก้ที่ทำได้จริง
            f.Add(new("RD-WHT-005", "Error",
                $"{missingTaxId} บรรทัดไม่มีเลขประจำตัวผู้เสียภาษีของผู้รับ.",
                "เติมเลขผู้เสียภาษี 13 หลักที่ผู้ติดต่อ (ผู้ถูกหัก) แล้วกด \"สร้างใหม่\" · บรรทัดรายการบัญชี (JE ล้วน) ที่ไม่มีผู้ถูกหัก: "
                + "ออก 50 ทวิ ให้ผู้รับ (ผูกผู้ติดต่อ) แล้วสร้างใหม่ หรือติ๊กบรรทัดนั้นออกถ้าไม่ใช่การจ่ายของงวดนี้."));
    }

    /// <summary>รอบ 275 แพ็ก B (T2 M2): ภ.พ.36 — ยอดหัว (OutputVat/NetVat/TotalIncome) ต้องเท่า Σ บรรทัดที่นับ (ตัวตั้งเดียว WhtReportTotals ·
    /// สูตรเดียวกับ TaxService.RecalcPp36Totals) · เดิมไม่มีการเทียบเลย</summary>
    private static void CheckPp36(TaxReport r, List<TaxComplianceFinding> f)
    {
        var (sumBase, sumVat) = Accounting.Helpers.WhtReportTotals.Of(r.Lines);
        if (Math.Abs(sumVat - r.OutputVat) > HeaderTolerance || Math.Abs(r.NetVat - r.OutputVat) > HeaderTolerance
            || Math.Abs(sumBase - r.TotalIncome) > HeaderTolerance)
            f.Add(new("RD-PP36-001", "Error",
                $"ยอด ภ.พ.36 ในรายการย่อย (ฐาน {sumBase:N2} · VAT {sumVat:N2}) ไม่เท่ากับยอดหัว (ฐาน {r.TotalIncome:N2} · VAT {r.OutputVat:N2} · นำส่ง {r.NetVat:N2}).",
                "กด \"บันทึก\" ที่รายงาน (ระบบคำนวณยอดหัวจากบรรทัดใหม่) หรือกด \"สร้างใหม่\"."));
        if (r.OutputVat < 0)
            f.Add(new("RD-PP36-002", "Warning", "VAT ที่ต้องนำส่ง (ภ.พ.36) ติดลบ — ตรวจใบลดหนี้ของงวด.", "ตรวจใบลดหนี้ของบริการต่างประเทศในงวด แล้วกด \"สร้างใหม่\"."));
    }

    private static void CheckPnd1(TaxReport r, List<TaxComplianceFinding> f)
    {
        // PND.1 = employee salary WHT — the recipient is always
        // individual employee. Most checks are structural (sums match).
        if (r.TotalIncome <= 0)
            f.Add(new("RD-PND1-001", "Warning",
                "TotalIncome = 0 — ตรวจสอบว่ามี payroll record เดือนนี้.", null));

        var (_, sumWht) = Accounting.Helpers.WhtReportTotals.Of(r.Lines);   // รอบ 273: ตัวตั้งเดียวกับยอดหัว
        if (Math.Abs(sumWht - r.TotalTaxWithheld) > HeaderTolerance)
            f.Add(new("RD-PND1-002", "Error",
                $"ผลรวมภาษีหัก ({sumWht:N2}) ไม่เท่ากับ TotalTaxWithheld ({r.TotalTaxWithheld:N2}).",
                null));
    }

    /// <summary>กำหนดยื่น e-Filing (แอปนี้ส่งออกรูปแบบ e-Filing จึงใช้กรอบที่ยาวกว่า)
    ///
    /// <para>⚠️ เดิมเขียนตารางเองที่นี่แล้วเหมา <b>ภ.พ.36 ไปรวมกับ ภ.พ.30</b>
    /// (<c>VAT or VatPp36 => AddDays(22)</c> = วันที่ 23) — ภ.พ.36 อยู่ใต้ §83/6
    /// ซึ่งกำหนด "ภายใน 7 วันนับแต่วันสิ้นเดือน" ⇒ e-Filing คือวันที่ <b>15</b>
    /// ⇒ ตัวตรวจนี้เตือนช้ากว่ากำหนดจริง 8 วันมาตลอด. ตารางย้ายไปอยู่ที่
    /// <see cref="Accounting.Helpers.TaxFilingDeadline"/> ตัวเดียวแล้ว</para>
    /// <para>รอบ 201 ทีม TX (B-7): ใช้วันที่เตือนของตารางกลาง (<c>WarnByFor</c>) — ภ.พ.36/ภ.ง.ด.54 = วันกระดาษ จนกว่าจะยืนยันว่ามาตรการ
    /// e-Filing +8 วันครอบสองแบบนี้ (ไม่ครอบแล้วเตือนตามวัน e-Filing = ช้า 8 วัน) · แบบอื่น = วัน e-Filing เท่าเดิม</para></summary>
    private static DateTime? DeadlineFor(TaxType type, int year, int month, IReadOnlySet<DateTime>? holidays)
        => Accounting.Helpers.TaxFilingDeadline.WarnByFor(type, year, month, holidays);
}
