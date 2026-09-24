using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **"งานนี้ทำได้เฉพาะเจ้าของบริษัท" — ตัวตัดสินตัวเดียว (pure)** · ฝ่ายค้านรอบ 193 รอบสอง W2-C3/W2-C4
///
/// <para>เดิมแต่ละ controller เขียนด่านเจ้าของเอง (<c>SubscriptionController.RequireOwnerAsync</c> · <c>IntegrationController</c> ·
/// <c>CompanyService.EnsureOwnerAccessAsync</c>) และหลาย endpoint ที่ doc-comment เขียนว่า "Owner only" ไม่มีด่านเลย
/// (<c>SensitivityController.SetRule</c> · <c>PaymentSettingsController</c> คีย์ลับ gateway + สลับ live) ⇒ ใช้ตัวนี้ผ่าน
/// <c>Filters/RequireOwnerAttribute</c> (attribute) หรือ <c>RequireOwnerAttribute.DenyAsync</c> (inline)</para>
///
/// <para>ลำดับ: คำขอจาก API key = ปฏิเสธก่อนเสมอ (แม้ตัวตนที่คีย์ถือเป็นเจ้าของ — <see cref="OwnerActionGuard"/>) ·
/// ผู้ดูแลแพลตฟอร์ม (claim SystemAdmin) ผ่าน · สมาชิกบทบาท Owner/SystemAdmin ของบริษัทนั้นผ่าน · อื่น ๆ รวมถึง
/// "ไม่ใช่สมาชิก" (role = null) = ปฏิเสธ (ไม่รู้ ≠ ผ่าน — DECISION_DOCTRINE §1)</para>
/// </summary>
public static class OwnerGateDecision
{
    public enum Outcome { Allow, DenyApiKey, DenyNotOwner }

    public static Outcome Decide(bool isApiKeyRequest, bool isPlatformAdmin, UserRole? companyRole)
    {
        if (isApiKeyRequest) return Outcome.DenyApiKey;
        if (isPlatformAdmin) return Outcome.Allow;
        return companyRole is UserRole.Owner or UserRole.SystemAdmin ? Outcome.Allow : Outcome.DenyNotOwner;
    }

    /// <summary>ข้อความ 403 ของ "ไม่ใช่เจ้าของ" — บอกว่าทำอะไรไม่ได้ เพราะอะไร และขอใคร</summary>
    public static string NotOwnerMessage(string verb, string? why = null)
        => $"ไม่มีสิทธิ์{verb} — ทำได้เฉพาะเจ้าของบริษัท"
           + (string.IsNullOrWhiteSpace(why) ? "" : $" ({why.Trim()})")
           + " กรุณาติดต่อเจ้าของบริษัท";
}
