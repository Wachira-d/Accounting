using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Implementations;

public partial class TaxService
{
    /// <summary>รหัสด่าน "ตัวตรวจก่อนยื่นพบ Error" ฝั่งเซิร์ฟเวอร์ (รอบ 275 แพ็ก B · T2 M1)</summary>
    public const string PreFileCheckRuleCode = "TAX-PRECHECK-ERROR-275";

    /// <summary>
    /// <b>ด่านเดียวของ "รายงานนี้ยื่นได้ไหม"</b> ที่ทุกทางยื่นเดิน — บันทึกการยื่น (<see cref="FileTaxReportUnlockedAsync"/> · หน้าเว็บ/API/มือถือ · ใต้ล็อกเดียวกับการยื่น)
    /// และปุ่มสร้างไฟล์ e-Filing จากรายงาน (<c>GenerateEFilingAsync</c> · ภ.ง.ด.2/3/53/54/ภ.พ.36) · รอบ 275 แพ็ก B (T2 M1 · M2 · H2 (3))
    ///
    /// <para>(1) <b>ตัวตรวจก่อนยื่น</b> (<see cref="Tax.TaxComplianceChecker"/>) — Error ที่อยู่ใน <see cref="Tax.TaxComplianceChecker.ServerBlockingCodes"/>
    /// (ยอดหัว ≠ Σ บรรทัด · ไม่มีเลขผู้เสียภาษี) ⇒ 400 · Error อื่น (เช่นยอด ภ.พ.30 ติดลบในเดือนที่ใบลดหนี้มากกว่ายอดขาย) คงเป็นคำเตือนบนหน้าตรวจ
    /// (ฝ่ายค้าน MED-3)</para>
    /// <para>(2) <b>ทะเบียน 50 ทวิ/เอกสารเปลี่ยนหลังสร้างร่าง</b> (ภ.ง.ด.2/3/53/54 · ภ.พ.36) — ตัวเทียบเดียวกับแถบร่างล้าสมัย (<see cref="DraftFreshnessAsync"/>) ⇒ 409
    /// "กดสร้างใหม่" · ภ.ง.ด.1 รุ่นเก่า ⇒ 409 ชี้ไปเมนูส่งออกของเงินเดือน</para>
    /// <para><b>เฉพาะร่าง</b> (ฝ่ายค้าน HIGH-1): รายงานที่ยื่น/บันทึกว่ายื่นแล้วสร้างไฟล์จากยอดที่บันทึกไว้ได้เสมอ — ทะเบียนขยับหลังยื่น ⇒ คืนข้อความเตือน
    /// "พิจารณายื่นเพิ่มเติม" (ไม่บล็อก · ไม่แตะตัวเลขที่ยื่น) · คืน null = ไม่มีคำเตือน</para>
    /// </summary>
    private async Task<string?> EnsureReportFileableAsync(Guid companyId, TaxReport report)
    {
        var usesRegister = report.TaxType == TaxType.WithholdingTax1
            || report.TaxType == TaxType.VatPp36
            || Accounting.Helpers.TaxReportDraftFreshness.UsesWhtSignature(report.TaxType);

        if (Accounting.Helpers.TaxFilingLockPolicy.DeclaredOrFiled(report.Status))
        {
            if (!usesRegister || report.TaxType == TaxType.WithholdingTax1) return null;
            return await DraftFreshnessAsync(companyId, report) is { } drift
                ? Accounting.Helpers.TaxReportDraftFreshness.FiledDriftNote(drift.Message ?? "")
                : null;
        }

        var check = await new Tax.TaxComplianceChecker(_db).CheckAsync(companyId, report.Id);
        var errors = check.Findings.Where(f => f.Severity == "Error" && Tax.TaxComplianceChecker.ServerBlockingCodes.Contains(f.Code)).ToList();
        if (errors.Count > 0)
        {
            var first = errors[0];
            throw new Accounting.Helpers.BusinessRuleException(
                $"ยื่นไม่ได้ — ตัวตรวจก่อนยื่นพบข้อผิดพลาด {errors.Count} ข้อ: {first.Code} {first.Message}"
                + (string.IsNullOrWhiteSpace(first.FixHint) ? "" : $" · ทางแก้: {first.FixHint}")
                + (errors.Count > 1 ? $" (และอีก {errors.Count - 1} ข้อ — กด \"ยื่นภาษี\" เพื่อดูทั้งหมด)" : ""),
                PreFileCheckRuleCode);
        }

        if (usesRegister && await DraftFreshnessAsync(companyId, report) is { } stale)
            throw new Accounting.Helpers.BusinessRuleException(
                Accounting.Helpers.TaxReportDraftFreshness.FileBlockMessage(stale.Message ?? ""),
                stale.RuleCode == Accounting.Helpers.TaxReportDraftFreshness.LegacyPnd1RuleCode
                    ? stale.RuleCode : Accounting.Helpers.TaxReportDraftFreshness.FileRuleCode,
                409);
        return null;
    }
}
