using System.Text.Json;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **คำนวณรายงานภาษีใหม่ — ร่องรอย + คำแนะนำ (pure · ทีม O 2026-10-22 · หลักการ #9 "persist แล้วต้องมี migration")**
///
/// <para>รายงาน ภ.ง.ด.50/51 ที่สร้างก่อนแก้ <see cref="ReportableJournal"/> เก็บยอดที่ "ใบกลับรายการถูกนับแต่ใบเดิมไม่ถูกนับ" ไว้แล้ว ·
/// <b>ไม่แก้ตัวเลขเงียบ ๆ</b>: ร่าง ⇒ ผู้ใช้กด "สร้างใหม่" (บันทึก audit ยอดเดิม → ยอดใหม่ ด้วย <see cref="AuditPayload"/>) ·
/// ยื่น/ล็อกแล้ว ⇒ ระบบไม่แตะ แสดงในรายงานทบทวน (<c>TaxService.GetCitRecomputeReviewAsync</c>) ให้ผู้ใช้พิจารณายื่นเพิ่มเติม</para>
/// </summary>
public static class TaxReportRecompute
{
    public const string AuditAction = "TaxReportRecomputed";

    public const string ActionNone = "None";
    public const string ActionRegenerate = "Regenerate";
    public const string ActionConsiderAmendedFiling = "ConsiderAmendedFiling";

    /// <summary>คำแนะนำต่อรายงานหนึ่งฉบับจากผลตัดสินความสด (<see cref="CitReportFreshness"/>) — ยอดตรง ⇒ ไม่ต้องทำอะไร ·
    /// ต่าง + ร่าง ⇒ สร้างใหม่ · ต่าง + ยื่นแล้ว ⇒ พิจารณายื่นเพิ่มเติม (ห้ามแตะตัวเลขที่ยื่นแล้ว)</summary>
    public static string SuggestedAction(CitReportFreshness.Level level)
        => level switch
        {
            CitReportFreshness.Level.Stale => ActionRegenerate,
            CitReportFreshness.Level.StaleAfterFiling => ActionConsiderAmendedFiling,
            _ => ActionNone,
        };

    /// <summary>ภาพตัวเลขของรายงานหนึ่งฉบับ (ใช้เป็น OldValues/NewValues ของแถว audit) — ฟิลด์ที่เป็นผลการคำนวณเท่านั้น ·
    /// "สร้างใหม่" ลบแถวเดิมแล้วสร้างแถวใหม่ (Id ใหม่) ⇒ <paramref name="replacesReportId"/> = Id ของฉบับเดิม (แถว audit ผูก EntityId = ฉบับใหม่)</summary>
    public static string AuditPayload(TaxReport r, string stage, Guid? replacesReportId = null)
        => JsonSerializer.Serialize(new
        {
            action = AuditAction,
            stage,
            reportId = r.Id,
            replacesReportId,
            taxType = r.TaxType.ToString(),
            year = r.Year,
            month = r.Month,
            status = r.Status.ToString(),
            totalIncome = r.TotalIncome,
            netVat = r.NetVat,
            citAmount = r.CitAmount,
            outputVat = r.OutputVat,
            inputVat = r.InputVat,
            totalTaxWithheld = r.TotalTaxWithheld,
            ruleCode = ReportableJournal.RuleCode,
        });
}
