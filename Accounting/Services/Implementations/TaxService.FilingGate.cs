using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Implementations;

public partial class TaxService
{
    /// <summary>รหัสด่าน "ตัวตรวจก่อนยื่นพบ Error" ฝั่งเซิร์ฟเวอร์ (รอบ 275 แพ็ก B · T2 M1)</summary>
    public const string PreFileCheckRuleCode = "TAX-PRECHECK-ERROR-275";

    /// <summary>
    /// <b>ด่านเดียวของ "รายงานนี้ยื่นได้ไหม"</b> ที่ทุกทางยื่นเดิน — บันทึกการยื่น (<see cref="FileTaxReportUnlockedAsync"/> · หน้าเว็บ/API/มือถือ)
    /// และปุ่มสร้างไฟล์ e-Filing จากรายงาน (<c>BuildPndAsync</c>/<c>BuildPp36Async</c>) · รอบ 275 แพ็ก B (T2 M1 · M2 · H2 (3))
    ///
    /// <para>(1) <b>ตัวตรวจก่อนยื่น</b> (<see cref="Tax.TaxComplianceChecker"/>) — เดิมเป็นงานของหน้าเว็บ (GET precheck) และเมื่อ precheck ล้มผู้ใช้กดยื่นต่อได้ ⇒
    /// ยื่นผ่าน API = ยื่นทั้งที่ยอดหัว ≠ บรรทัด · ตอนนี้ Error ⇒ 400 พร้อมรหัสและทางแก้ของข้อแรก</para>
    /// <para>(2) <b>ทะเบียน 50 ทวิ/เอกสารเปลี่ยนหลังสร้างร่าง</b> (ภ.ง.ด.2/3/53/54 · ภ.พ.36) — ตัวเทียบเดียวกับแถบร่างล้าสมัย (<see cref="DraftFreshnessAsync"/>) ⇒ 409
    /// "กดสร้างใหม่" · ภ.ง.ด.1 รุ่นเก่า ⇒ 409 ชี้ไปเมนูส่งออกของเงินเดือน (แบบเดียวกับ <c>WhtCreditClaimStamp.ReportOutOfDate</c> ของ ภ.ง.ด.50)</para>
    /// <para>ภ.พ.30/ภ.ง.ด.50/ภ.ง.ด.91 เดินข้อ (1) เท่านั้น (ภ.พ.30 มีด่านเอกสารตาย · ภ.ง.ด.50 มีด่านเครดิต ในตัวยื่นอยู่แล้ว) · อ่านอย่างเดียว</para>
    /// </summary>
    private async Task EnsureReportFileableAsync(Guid companyId, TaxReport report)
    {
        var check = await new Tax.TaxComplianceChecker(_db).CheckAsync(companyId, report.Id);
        var errors = check.Findings.Where(f => f.Severity == "Error").ToList();
        if (errors.Count > 0)
        {
            var first = errors[0];
            throw new Accounting.Helpers.BusinessRuleException(
                $"ยื่นไม่ได้ — ตัวตรวจก่อนยื่นพบข้อผิดพลาด {errors.Count} ข้อ: {first.Code} {first.Message}"
                + (string.IsNullOrWhiteSpace(first.FixHint) ? "" : $" · ทางแก้: {first.FixHint}")
                + (errors.Count > 1 ? $" (และอีก {errors.Count - 1} ข้อ — กด \"ยื่นภาษี\" เพื่อดูทั้งหมด)" : ""),
                PreFileCheckRuleCode);
        }

        if (report.TaxType == TaxType.WithholdingTax1
            || report.TaxType == TaxType.VatPp36
            || Accounting.Helpers.TaxReportDraftFreshness.UsesWhtSignature(report.TaxType))
        {
            if (await DraftFreshnessAsync(companyId, report) is { } stale)
                throw new Accounting.Helpers.BusinessRuleException(
                    Accounting.Helpers.TaxReportDraftFreshness.FileBlockMessage(stale.Message ?? ""),
                    stale.RuleCode == Accounting.Helpers.TaxReportDraftFreshness.LegacyPnd1RuleCode
                        ? stale.RuleCode : Accounting.Helpers.TaxReportDraftFreshness.FileRuleCode,
                    409);
        }
    }
}
