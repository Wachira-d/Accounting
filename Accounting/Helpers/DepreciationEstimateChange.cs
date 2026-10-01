using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>เปลี่ยนประมาณการค่าเสื่อมราคา<b>ไปข้างหน้า</b> (TFRS for NPAEs บทที่ 10) — อายุใช้งาน · มูลค่าซาก · วิธีคิดค่าเสื่อม
/// ของสินทรัพย์ที่เริ่มคิดค่าเสื่อมแล้ว · ตัวตัดสินเดียวของปุ่ม “ปรับอายุการใช้งาน” (รอบ 201 ทีม IN · A-IN3 · คำตัดสินข้อ 38)
///
/// ═══ ที่มา ═══
/// หน้าแก้ไขสินทรัพย์ปฏิเสธการแก้อายุ/ซาก/วิธีหลังมีค่าเสื่อมลงบัญชี (<see cref="FixedAssetValuationEdit"/> — ถูก: ห้ามแก้ย้อนหลัง)
/// แต่ทางไปต่อที่มาตรฐานกำหนด ("เปลี่ยนประมาณการ = มีผลไปข้างหน้า") มีแค่อายุ/ซาก — เปลี่ยน<b>วิธีคิด</b> (เส้นตรง ↔ ยอดลดลง)
/// ไม่มีทางทำเลย และการ "ยืนยันว่าทบทวนแล้วค่าเดิม" ต้องส่งอายุเดิมซึ่งไปสร้างฐานใหม่ทั้งที่ไม่มีอะไรเปลี่ยน
///
/// ═══ กติกา ═══
/// • มีผลตั้งแต่งวดถัดจากงวดที่ลงบัญชีล่าสุด: ฐานใหม่ = มูลค่าตามบัญชีคงเหลือ − ซากใหม่ · อายุคงเหลือ = อายุใหม่ − งวดที่คิดไปแล้ว ·
///   งวดที่ลงบัญชีแล้วไม่ถูกแตะ (ผู้เรียกสร้างเฉพาะแผนที่ยังไม่ลง)
/// • ไม่มีค่าใดเปลี่ยน = <b>บันทึกว่าทบทวนแล้ว</b> (ประทับวันทบทวน) โดยไม่สร้างฐานใหม่ — ยอดลดลงจะไม่เปลี่ยนอัตราเงียบ ๆ
/// • หยุดคิดค่าเสื่อม (วิธี “ไม่คิด”) ไม่ใช่การเปลี่ยนประมาณการ ⇒ ปฏิเสธพร้อมทางไปต่อ (จำหน่าย/ตัดจำหน่าย) · ผังที่ดิน/งานระหว่างก่อสร้าง
///   ไม่มีประมาณการให้ทบทวน
/// </summary>
public static class DepreciationEstimateChange
{
    public const string RuleCode = "TFRS-NPAES-10-ESTIMATE";

    /// <summary>มีค่าใดเปลี่ยนจริงหรือไม่ (ไม่เปลี่ยน = ยืนยันการทบทวนอย่างเดียว)</summary>
    public static bool IsChange(DepreciationMethod currentMethod, int currentLifeMonths, decimal currentSalvage,
        DepreciationMethod newMethod, int newLifeMonths, decimal newSalvage)
        => currentMethod != newMethod || currentLifeMonths != newLifeMonths || currentSalvage != newSalvage;

    /// <summary>null = ทำได้ · ข้อความ = ปฏิเสธ (ไทย พร้อมทางไปต่อ)</summary>
    /// <param name="elapsedMonths">จำนวนงวดที่คิดค่าเสื่อมไปแล้ว (นับจากงวดที่ลงบัญชีล่าสุด)</param>
    /// <param name="netBookValue">มูลค่าตามบัญชีคงเหลือ ณ งวดที่ลงบัญชีล่าสุด</param>
    public static string? Problem(DepreciationMethod currentMethod, DepreciationMethod newMethod, int newLifeMonths,
        decimal newSalvage, int elapsedMonths, decimal netBookValue, bool accountNonDepreciable)
    {
        if (currentMethod == DepreciationMethod.None)
            return "สินทรัพย์นี้ไม่คิดค่าเสื่อมราคา (ที่ดิน/งานระหว่างก่อสร้าง) — ไม่มีอายุใช้งานให้ทบทวน · ถ้าตั้งผิดตั้งแต่ต้น ให้แก้ที่หน้าแก้ไขก่อนเริ่มคิดค่าเสื่อม";
        if (!Enum.IsDefined(typeof(DepreciationMethod), newMethod))
            return "วิธีคิดค่าเสื่อมราคาไม่ถูกต้อง — เลือกเส้นตรง / ยอดลดลง / ยอดลดลงสองเท่า";
        if (newMethod == DepreciationMethod.None)
            return "การหยุดคิดค่าเสื่อมไม่ใช่การเปลี่ยนประมาณการ — ถ้าเลิกใช้สินทรัพย์ ให้ใช้ “จำหน่าย” หรือ “ตัดจำหน่าย” แทน";
        if (accountNonDepreciable)
            return "ผังบัญชีของสินทรัพย์นี้เป็นที่ดิน/งานระหว่างก่อสร้าง คิดค่าเสื่อมราคาไม่ได้ (พ.ร.ฎ.145) — แก้ผังบัญชีให้ตรงกับสินทรัพย์จริงก่อน";
        if (newLifeMonths <= 0)
            return "อายุการใช้งานต้องมากกว่า 0 เดือน";
        if (newSalvage < 0m)
            return "มูลค่าซากต้องไม่ติดลบ";
        if (newLifeMonths - elapsedMonths <= 0)
            return $"อายุใหม่ {newLifeMonths} เดือน สั้นกว่าหรือเท่ากับที่คิดค่าเสื่อมไปแล้ว ({elapsedMonths} งวด) — ถ้าต้องการตัดจบทันที ให้ใช้ “ตัดจำหน่าย” แทน";
        if (newSalvage > netBookValue)
            return $"มูลค่าซากใหม่ {newSalvage:N2} สูงกว่ามูลค่าตามบัญชีคงเหลือ {netBookValue:N2} — ซากต้องไม่เกินมูลค่าคงเหลือ "
                + "(ถ้ามูลค่าสินทรัพย์เพิ่มขึ้นจริง ให้ใช้ “ตีราคาใหม่”)";
        return null;
    }

    /// <summary>ฐานที่เหลือให้คิดค่าเสื่อม = มูลค่าตามบัญชีคงเหลือ − ซากใหม่ (ปัด 2 ตำแหน่ง AwayFromZero)</summary>
    public static decimal RemainingBase(decimal netBookValue, decimal newSalvage)
        => Math.Round(netBookValue - newSalvage, 2, MidpointRounding.AwayFromZero);
}
