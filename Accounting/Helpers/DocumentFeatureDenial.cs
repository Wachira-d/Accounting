using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 259 (B-L1) — ข้อความ "สร้างเอกสารไม่ได้เพราะแพ็กเกจ" ที่บอกสาเหตุจริง · เดิม <c>"ไม่มีสิทธิ์ใช้ระบบเอกสาร"</c> ทุกกรณี ⇒ บริษัทที่แพ็กเกจหมดอายุเห็นบนแถวรายการประจำ
/// ว่า "ไม่มีสิทธิ์" ทุก 15 นาที โดยไม่รู้ว่าต้องต่ออายุ (ผู้ใช้ไปไล่ดูสิทธิ์ผู้ใช้ผิดทาง) · ข้อความที่ระบุสาเหตุต้องตรวจสาเหตุนั้นจริง (F2 ข้อ 7) —
/// สถานะมาจากด่านแพ็กเกจตัวเดียวกับ middleware (<c>ISubscriptionService.GetGateStateAsync</c>) · ไม่รู้สถานะ = ข้อความกลาง (ไม่เดา)
/// </summary>
public static class DocumentFeatureDenial
{
    public const string RuleCodeInactive = "SUBSCRIPTION-INACTIVE";
    public const string RuleCodeNoFeature = "SUBSCRIPTION-NO-DOCUMENT-ENGINE";

    /// <summary>สถานะที่ "ใช้งานไม่ได้เพราะแพ็กเกจหยุด" (ไม่ใช่แพ็กเกจไม่มีฟีเจอร์)</summary>
    public static bool IsInactive(SubscriptionStatus? status) => status is SubscriptionStatus.Expired or SubscriptionStatus.Cancelled or SubscriptionStatus.Suspended;

    public static (string Message, string RuleCode) Describe(SubscriptionStatus? status, bool inactiveReadOnly = false)
    {
        if (inactiveReadOnly || IsInactive(status))
            return ($"แพ็กเกจของบริษัท{StatusLabel(status, inactiveReadOnly)} — สร้างเอกสารใหม่ไม่ได้จนกว่าจะต่ออายุ/ชำระที่หน้า \"แพ็กเกจและการชำระเงิน\" · "
                    + "งานอัตโนมัติ (รายการประจำ) หยุดระหว่างนี้ ต่ออายุแล้วรอบถัดไปออกเฉพาะงวดล่าสุดให้เอง (งวดที่ค้างไม่ถูกไล่ออกย้อนหลัง)", RuleCodeInactive);
        if (status == null)
            return ("ไม่มีสิทธิ์ใช้ระบบเอกสาร (ตรวจสถานะแพ็กเกจไม่ได้) — เปิดหน้า \"แพ็กเกจและการชำระเงิน\" เพื่อตรวจ หรือติดต่อผู้ดูแลระบบ", RuleCodeNoFeature);
        return ("แพ็กเกจปัจจุบันไม่มีระบบเอกสาร — อัปเกรดแพ็กเกจที่หน้า \"แพ็กเกจและการชำระเงิน\" หรือติดต่อผู้ดูแลระบบ", RuleCodeNoFeature);
    }

    private static string StatusLabel(SubscriptionStatus? s, bool inactiveReadOnly) => s switch
    {
        SubscriptionStatus.Expired => "หมดอายุแล้ว",
        SubscriptionStatus.Cancelled => "ถูกยกเลิกแล้ว",
        SubscriptionStatus.Suspended => "ถูกระงับ",
        _ => inactiveReadOnly ? "ถูกระงับ/ยกเลิก" : "ใช้งานไม่ได้",
    };
}
