using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// <b>ประเภทเงินมัดจำ</b> ต่อบริษัท (รอบ 194 · spec S1/S4/S8) — "เงินก้อนนี้คืออะไร" (<see cref="Nature"/> = ตัวกำหนด VAT)
/// + "บันทึกแบบไหน" (<see cref="VatTreatment"/> = วิธีบันทึก) + บัญชีหนี้สิน/บัญชีริบ
///
/// <para>ตัวตัดสินตัวเดียว = <c>Helpers/DepositPolicyResolver.ResolveKind</c> (ลำดับ: ประเภทบนใบ → ประเภทของช่องทาง →
/// ค่าเดิมของช่องทาง → ประเภทเริ่มต้นบริษัท (<see cref="IsDefault"/>) → ค่าตั้งต้นบริษัท → ประเภทธุรกิจ) ·
/// รูปใบตาม <c>Helpers/DepositDocumentShaping.Apply</c> · ค่าเริ่มต้นต่อประเภทธุรกิจ = <c>Helpers/DepositKindSeed</c></para>
///
/// <para>⚠️ ใบที่ออกแล้วตรึงสำเนาลงใบ (<c>Document.DepositKindId/DepositNature/DepositKindName</c>) — แก้ประเภททีหลัง
/// <b>ไม่</b>เปลี่ยนใบเดิม (§86/4 ห้ามแก้ย้อนหลัง)</para>
/// </summary>
public class DepositKind : TenantEntity
{
    /// <summary>รหัสสั้น (ADVANCE · SECURITY · RENT-ADV · LP-xxxxxxxx) — คู่ค้าส่งมาเป็น <c>DepositKindCode</c> ·
    /// unique ต่อบริษัท (แถวที่ยังไม่ลบ)</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>ชื่อที่ผู้ใช้เห็น (ตรึงลงใบเป็น <c>Document.DepositKindName</c>)</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>ลักษณะเงิน — ตัวกำหนด VAT (ไม่ใช่โหมด)</summary>
    public DepositNature Nature { get; set; } = DepositNature.PartOfPrice;

    /// <summary>วิธีบันทึก — <b>null = ตามค่าตั้งต้นบริษัท/ประเภทธุรกิจ</b> (ประเภท ADVANCE ที่ seed ใช้ค่านี้ เพื่อให้ปุ่มตั้งค่าเดิม
    /// "ตั้งค่า → ภาษี" ยังมีผล — ไม่มีสำเนาสองที่ที่ต้องตามให้ตรงกัน)</summary>
    public DepositVatTreatment? VatTreatment { get; set; }

    /// <summary>บัญชีหนี้สินที่พักเงิน — null = ค่าตามระบบ (ราคา → 217xx ตาม AutoPost เดิม · เงินประกัน → 21530 ถ้าผังมี ไม่งั้น 21620
    /// ตัดสินที่ <c>DepositPolicyResolver.SecurityLiabilityAccountCode</c>)</summary>
    public string? LiabilityAccountCode { get; set; }

    /// <summary>บัญชีรายได้ตอน "ริบเป็นค่าเสียหาย" — null = บัญชีของเส้นริบเดิม (ที่พัก: CancellationFeeAccountCode → รายได้ค่าห้อง)</summary>
    public string? ForfeitAccountCode { get; set; }

    /// <summary>เหตุผลที่เลือกโหมดเลื่อน VAT กับเงินที่เป็นส่วนหนึ่งของราคา (spec S2) — ว่าง = ปฏิเสธการบันทึก
    /// (<c>DepositPolicyResolver.KindProblem</c>) · พิมพ์เป็นหมายเหตุบนใบ (<c>Document.DepositPolicyNote</c>)</summary>
    public string? PolicyReason { get; set; }

    /// <summary>คำอธิบายเพิ่มเติม (แสดงในหน้าตั้งค่า)</summary>
    public string? Description { get; set; }

    /// <summary>ประเภทเริ่มต้นของบริษัท (ชั้นที่ 4 ของลำดับ) — มีได้ตัวเดียวต่อบริษัท (หน้าตั้งค่าเป็นผู้คุม)</summary>
    public bool IsDefault { get; set; }

    /// <summary>ปิดใช้ = ไม่ถูกเลือกโดยตัวตัดสิน (ตกไปชั้นถัดไป) · ใบเดิมที่อ้างถึงยังอยู่ครบ</summary>
    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>กุญแจของแถวที่ระบบ seed (<c>sys:ADVANCE</c> · <c>sys:SECURITY</c> · <c>sys:RENT-ADV</c> · <c>lp:{propertyId}</c>) —
    /// unique ต่อบริษัท ⇒ seed ซ้ำได้ (ON CONFLICT DO NOTHING) และไม่ seed คืนแถวที่ผู้ใช้ลบทิ้ง · null = ผู้ใช้สร้างเอง</summary>
    public string? SeedKey { get; set; }
}
