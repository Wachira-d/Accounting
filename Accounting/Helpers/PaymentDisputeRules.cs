using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 228 (PLATFORM_OMISE_AUDIT §5.4) — <b>ข้อพิพาท/chargeback</b>: ตัวตัดสินเดียวของป้าย · "ต้องให้แอดมินจัดการไหม" · ข้อความประวัติ · pure
/// <para><b>ทำไมไม่เปลี่ยนสถานะเงินเอง:</b> แพ้ข้อพิพาท = เงินถูกดึงคืนตอนรอบโอน (ผู้ให้บริการหักจากยอดโอน) — การตัดสินพักสิทธิ์/ออกใบลดหนี้/ลงค่าเสียหายเป็นของคน
/// (สถานะปลายทางที่ระบบประทับเองอันตรายกว่าไม่ตอบ — F2 ข้อ 3) · ระบบบันทึก + เตือน + แสดงบนรายการเท่านั้น</para>
/// </summary>
public static class PaymentDisputeRules
{
    public const string RuleCode = "GW-DISPUTE";

    public static string Label(PaymentDisputeState? s) => s switch
    {
        PaymentDisputeState.Open => "มีข้อพิพาท (รอส่งหลักฐาน)",
        PaymentDisputeState.UnderReview => "ข้อพิพาท — ธนาคารกำลังพิจารณา",
        PaymentDisputeState.Won => "ข้อพิพาท — ชนะ (เงินคงอยู่)",
        PaymentDisputeState.Lost => "ข้อพิพาท — แพ้ (เงินถูกดึงคืน)",
        PaymentDisputeState.Closed => "ข้อพิพาทปิดแล้ว (ตรวจผลที่แดชบอร์ดผู้ให้บริการ)",
        _ => "",
    };

    /// <summary>ต้องให้แอดมินจัดการไหม — เปิด/พิจารณา (ส่งหลักฐานภายในกำหนดของผู้ให้บริการ) · แพ้ (ตัดสินพักสิทธิ์/ใบลดหนี้/ค่าเสียหาย) · ปิดไม่รู้ผล</summary>
    public static bool NeedsAttention(PaymentDisputeState? s)
        => s is PaymentDisputeState.Open or PaymentDisputeState.UnderReview or PaymentDisputeState.Lost or PaymentDisputeState.Closed;

    /// <summary>ทางไปต่อของแอดมินต่อสถานะ (ข้อความเดียว)</summary>
    public static string? NextStep(PaymentDisputeState? s) => s switch
    {
        PaymentDisputeState.Open or PaymentDisputeState.UnderReview
            => "ส่งหลักฐานการให้บริการ (ใบกำกับ/ใบเสร็จ · ประวัติการใช้งาน · การยอมรับนโยบาย) ที่แดชบอร์ดผู้ให้บริการก่อนกำหนด",
        PaymentDisputeState.Lost
            => "เงินถูกดึงคืนในรอบโอนถัดไป — ตัดสินพักสิทธิ์/ออกใบลดหนี้อ้างใบเดิม และลงค่าเสียหายตอนบันทึกรอบโอน",
        PaymentDisputeState.Closed => "ตรวจผลที่แดชบอร์ดผู้ให้บริการแล้วบันทึกด้วยมือถ้ามีผลต่อเงิน",
        _ => null,
    };

    /// <summary>สถานะปลายทาง (ผลออกแล้ว) — event ที่มาช้ากว่าห้ามถอยกลับเป็น "เปิด/พิจารณา" (ฝ่ายค้าน S4)</summary>
    public static bool IsFinal(PaymentDisputeState? s) => s is PaymentDisputeState.Won or PaymentDisputeState.Lost or PaymentDisputeState.Closed;

    /// <summary>ควรบันทึก event นี้ไหม (กันประวัติ/กระดิ่งซ้ำเมื่อ webhook ส่งซ้ำ + กันลำดับสลับ) · pure (ฝ่ายค้าน S4):
    /// เลขข้อพิพาทใหม่ที่ต่างจากเดิม = ข้อพิพาทใหม่ ⇒ บันทึก · เลขว่าง/เท่าเดิม = ข้อพิพาทเดิม ⇒ ผลออกแล้ว (ชนะ/แพ้/ปิด) ห้ามถอยเป็นเปิด/พิจารณา ·
    /// สถานะ+ยอดเท่าเดิม = ส่งซ้ำ ⇒ ไม่บันทึก</summary>
    public static bool ShouldApply(string? currentRef, PaymentDisputeState? currentState, decimal? currentAmount,
        string? newRef, PaymentDisputeState newState, decimal newAmount)
    {
        var sameDispute = string.IsNullOrWhiteSpace(newRef) || string.IsNullOrWhiteSpace(currentRef)
                          || string.Equals(currentRef, newRef, StringComparison.Ordinal);
        if (!sameDispute) return true;
        if (IsFinal(currentState) && !IsFinal(newState)) return false;
        return currentState != newState || currentAmount != newAmount;
    }

    /// <summary>ยังต้องให้แอดมินจัดการไหม (หลังรับทราบ) — รับทราบแล้วหลังการเปลี่ยนครั้งล่าสุด ⇒ ไม่ต้อง (ฝ่ายค้าน S5)</summary>
    public static bool NeedsAttention(PaymentDisputeState? s, DateTime? updatedAt, DateTime? acknowledgedAt)
        => NeedsAttention(s) && !(acknowledgedAt != null && (updatedAt == null || acknowledgedAt >= updatedAt));
}
