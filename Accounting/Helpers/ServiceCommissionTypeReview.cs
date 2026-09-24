using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ความชัดเจนของ "ประเภทคอมมิชชัน" ของขั้นตอนบริการ POS (แพ็คเกจ)</summary>
public enum ServiceCommissionTypeClarity
{
    /// <summary>ชัด — ยืนยันหลังแก้ฟอร์มแล้ว หรือเป็นค่าที่ฟอร์มเก่าผลิตไม่ได้ (Percentage)</summary>
    Clear = 0,
    /// <summary>ค่าในฐานไม่อยู่ใน enum (0 จากฟอร์มเก่าที่ตั้งใจ "คงที่") — ตอนขายระบบคิดเป็น<b>เปอร์เซ็นต์</b></summary>
    UndefinedValue = 1,
    /// <summary>ค่า 1 (Fixed) ที่บันทึกก่อนแก้ฟอร์ม — อาจตั้งใจ "เปอร์เซ็นต์" จากฟอร์มเก่า หรือ "คงที่" จาก API/ค่าเริ่มต้น</summary>
    AmbiguousLegacy = 2,
}

/// <summary>
/// ตัวตัดสินเดียวว่า "ประเภทคอมมิชชันของขั้นตอนนี้เชื่อได้ไหม" (รอบ 193 · ฝ่ายค้าน M2)
///
/// <para><b>ที่มา</b>: ฟอร์ม <c>pos-packages.html</c> ก่อนรอบ 193 ส่ง <c>parseInt</c> ของ option <c>0/1</c>
/// ขณะที่ enum คือ <c>Fixed=1 · Percentage=2</c> ⇒ เลือก "คงที่" เก็บ 0 (ไม่อยู่ใน enum — เส้นขาย
/// <c>comp.CommissionType == Fixed ? … : %</c> จึงคิดเป็น<b>เปอร์เซ็นต์</b>) · เลือก "เปอร์เซ็นต์" เก็บ 1 (= Fixed ⇒
/// คิดเป็น<b>จำนวนเงิน</b>) · ทั้งสองทิศกลับด้านกับที่ผู้ใช้เลือก</para>
///
/// <para><b>ห้ามแปลงข้อมูลเก่าอัตโนมัติ</b> (คำสั่งรอบ 193): 0 → Fixed แน่นอน แต่ 1 แยกไม่ออกว่ามาจากฟอร์มเก่า
/// (ตั้งใจ %) หรือจาก API/ค่าเริ่มต้นของคอลัมน์ (ตั้งใจคงที่) — เจ้าของต้องตัดสิน ⇒ ตัวนี้แค่<b>ติดป้าย</b>
/// ให้หน้าเว็บบังคับผู้ใช้เลือกใหม่ และให้รายงานนับแถวที่ต้องตรวจ · การบันทึกประเภทผ่านฟอร์ม/API ใหม่
/// ประทับ <c>CommissionTypeConfirmedAt</c> ⇒ แถวนั้นพ้นป้าย</para>
/// </summary>
public static class ServiceCommissionTypeReview
{
    public const string ReviewPrompt = "กรุณาตรวจประเภทคอมมิชชันแล้วบันทึกใหม่";

    public static ServiceCommissionTypeClarity Judge(CommissionType storedType, DateTime? confirmedAt)
    {
        if (!Enum.IsDefined(storedType)) return ServiceCommissionTypeClarity.UndefinedValue;
        if (confirmedAt.HasValue) return ServiceCommissionTypeClarity.Clear;
        // ฟอร์มเก่าผลิตได้แค่ 0/1 ⇒ Percentage (2) มาจาก API ที่ส่งชื่อ/เลขถูกเท่านั้น
        return storedType == CommissionType.Percentage
            ? ServiceCommissionTypeClarity.Clear
            : ServiceCommissionTypeClarity.AmbiguousLegacy;
    }

    public static bool NeedsReview(ServiceCommissionTypeClarity c) => c != ServiceCommissionTypeClarity.Clear;

    /// <summary>ข้อความบนแถว — บอกทั้ง "ทำไมไม่ชัด" และ "ตอนนี้ระบบคิดแบบไหนอยู่" (ห้ามเงียบ)</summary>
    public static string? Note(ServiceCommissionTypeClarity c) => c switch
    {
        ServiceCommissionTypeClarity.UndefinedValue =>
            "ประเภทคอมมิชชันในฐานข้อมูลไม่ใช่ค่าที่ระบบรู้จัก (ค่า 0 จากฟอร์มเก่า ซึ่งผู้บันทึกน่าจะเลือก \"คงที่\") — "
            + "ตอนขายระบบคิดเป็น \"เปอร์เซ็นต์\" · " + ReviewPrompt,
        ServiceCommissionTypeClarity.AmbiguousLegacy =>
            "ขั้นตอนนี้บันทึกก่อนแก้ฟอร์ม (รอบ 193) — ค่า \"คงที่\" ที่เห็นอาจมาจากการเลือก \"เปอร์เซ็นต์\" ในฟอร์มเก่า "
            + "(ตอนขายระบบคิดเป็นจำนวนเงินคงที่) · " + ReviewPrompt,
        _ => null,
    };

    /// <summary>ด่านค่าที่รับเข้า — enum ที่ไม่มีในระบบ (เช่นเลข 0 ผ่าน API) ต้องถูกปฏิเสธเป็นภาษาไทย
    /// ไม่ใช่เก็บเงียบแล้วไปคิดเป็นเปอร์เซ็นต์ตอนขาย</summary>
    public static void EnsureDefined(CommissionType type)
    {
        if (!Enum.IsDefined(type))
            throw new BusinessRuleException(
                $"ประเภทคอมมิชชัน \"{(int)type}\" ไม่อยู่ในรายการที่ระบบรองรับ — เลือกได้: คงที่ (Fixed) · เปอร์เซ็นต์ (Percentage)",
                "POS-COMMISSION-TYPE");
    }
}
