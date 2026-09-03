using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

/// <summary>ผลการตรวจสิทธิ์ 1 ความสามารถ — **ต้องตอบได้เสมอว่า "ทำอะไรต่อได้"**
/// (กติกา CLAUDE.md: ทุกด่านที่ปฏิเสธต้องมีทางไปต่อ ห้ามตันเงียบ)</summary>
/// <param name="Allowed">ใช้ได้ไหม</param>
/// <param name="Reason">เหตุผลภาษาไทยพร้อมโชว์ผู้ใช้ตรง ๆ (null เมื่อใช้ได้)</param>
/// <param name="UpgradeHint">ทางไปต่อ: "เปิดใช้ +100 บาท/เดือน" / "อัปเกรดเป็น Pro"</param>
/// <param name="AddOnCode">รหัส add-on ที่ต้องเปิด (ให้หน้าเว็บพาไปหน้าเปิดได้ตรง ๆ)</param>
/// <param name="Price">ราคาต่อเดือน/ต่อหน่วยที่จะโดน ถ้าเปิด (null = ยังไม่ตั้งราคา)</param>
/// <param name="InTrial">อยู่ในช่วงทดลองใช้ฟรี</param>
/// <param name="TrialUntil">ทดลองใช้ถึงเมื่อไร</param>
public record EntitlementResult(
    bool Allowed,
    string? Reason = null,
    string? UpgradeHint = null,
    string? AddOnCode = null,
    decimal? Price = null,
    bool InTrial = false,
    DateTime? TrialUntil = null)
{
    public static EntitlementResult Ok(bool inTrial = false, DateTime? trialUntil = null)
        => new(true, null, null, null, null, inTrial, trialUntil);
}

/// <summary>
/// **resolver สิทธิ์ตัวเดียวของระบบ** (LODGING_LICENSING_PLAN.md §6)
///
/// รวมสองชั้นที่เดิมไม่คุยกันให้เป็นทางเข้าเดียว:
///   1. ความสามารถที่มากับ**แพ็กเกจบัญชี** (`FeatureFlags` bitmask)
///   2. **add-on ที่ขายแยก** (`CompanyFeature` string code)
///
/// ⚠️ ห้ามคำนวณเงื่อนไข expiry/grace/pool เองซ้ำ — ต้องเรียก
/// `ISubscriptionService.GetEffectivePlanAsync` เป็น dependency เสมอ
/// (บทเรียน CLAUDE.md: "กฎสองข้อที่มองข้อมูลคนละชุดจะเถียงกันเองต่อหน้าผู้ใช้")
/// </summary>
public interface IEntitlementService
{
    /// <summary>ตรวจ 1 ความสามารถ — รับได้ทั้งชื่อบิต ("Payroll") และรหัส add-on
    /// ("lodging.guest-portal") โดยตัวมันแยกเองว่าเป็นชนิดไหน</summary>
    Task<EntitlementResult> CheckAsync(Guid companyId, string code, CancellationToken ct = default);

    /// <summary>ตรวจบิตของแพ็กเกจโดยตรง (ทางลัดสำหรับโค้ดที่ถือ enum อยู่แล้ว)</summary>
    Task<EntitlementResult> CheckAsync(Guid companyId, FeatureFlags flag, CancellationToken ct = default);

}
