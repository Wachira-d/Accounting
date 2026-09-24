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
        var deadline = DeadlineFor(report.TaxType, report.Year, report.Month);
        if (deadline.HasValue && DateTime.UtcNow.Date > deadline.Value)
            findings.Add(new("RD-DEADLINE", "Warning",
                $"พ้นกำหนดยื่นแล้ว ({deadline:yyyy-MM-dd}). อาจมีเบี้ยปรับ.",
                "เตรียมเงินค่าปรับ + คำชี้แจงเหตุล่าช้า ถ้ามี."));

        // ── Per-form checks ──────────────────────────────────────────
        switch (report.TaxType)
        {
            case TaxType.VAT: CheckVatPp30(report, findings); break;
            case TaxType.WithholdingTax3:
            case TaxType.WithholdingTax53: CheckPnd3_53(report, findings); break;
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

    private static void CheckPnd3_53(TaxReport r, List<TaxComplianceFinding> f)
    {
        // PND.3 = individual recipients; PND.53 = juristic recipients.
        // The forms differ by recipient type but share the structural
        // invariant: tax withheld = sum of line withholdings.
        if (r.TotalTaxWithheld <= 0 && r.TotalIncome <= 0)
            f.Add(new("RD-WHT-001", "Warning",
                "ทั้ง TotalIncome และ TotalTaxWithheld = 0 — รายงานเปล่า.",
                "ถ้าไม่มียอด withhold ในเดือนนี้ ไม่จำเป็นต้องยื่น."));

        var sumWht = r.Lines.Sum(l => l.TaxAmount);
        if (sumWht > 0 && Math.Abs(sumWht - r.TotalTaxWithheld) > 0.50m)
            f.Add(new("RD-WHT-002", "Error",
                $"ผลรวมภาษีหัก ณ ที่จ่ายในรายการย่อย ({sumWht:N2}) ไม่เท่ากับ TotalTaxWithheld ({r.TotalTaxWithheld:N2}).",
                "กดคำนวณใหม่หรือเพิ่ม/ลบบรรทัดให้ครบ."));

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
        var missingTaxId = r.Lines.Count(l => string.IsNullOrWhiteSpace(l.TaxPayerId));
        if (missingTaxId > 0)
            f.Add(new("RD-WHT-005", "Error",
                $"{missingTaxId} บรรทัดไม่มีเลขประจำตัวผู้เสียภาษีของผู้รับ.",
                "เปิดบรรทัดและเติมเลข 13 หลักทุกรายการก่อนยื่น."));
    }

    private static void CheckPnd1(TaxReport r, List<TaxComplianceFinding> f)
    {
        // PND.1 = employee salary WHT — the recipient is always
        // individual employee. Most checks are structural (sums match).
        if (r.TotalIncome <= 0)
            f.Add(new("RD-PND1-001", "Warning",
                "TotalIncome = 0 — ตรวจสอบว่ามี payroll record เดือนนี้.", null));

        var sumWht = r.Lines.Sum(l => l.TaxAmount);
        if (Math.Abs(sumWht - r.TotalTaxWithheld) > 0.50m)
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
    /// <see cref="Accounting.Helpers.TaxFilingDeadline"/> ตัวเดียวแล้ว</para></summary>
    private static DateTime? DeadlineFor(TaxType type, int year, int month)
        => Accounting.Helpers.TaxFilingDeadline.EFilingFor(type, year, month);
}
