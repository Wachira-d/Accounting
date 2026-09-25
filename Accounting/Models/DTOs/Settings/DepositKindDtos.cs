using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Settings;

/// <summary>บันทึกประเภทเงินมัดจำ (POST สร้าง · PUT แทนค่าทั้งก้อน) — ทุกช่อง nullable: service ตรวจเองแล้วตอบข้อความไทย
/// (ไม่ปล่อยให้ ASP.NET ตีกลับด้วยชื่อ property ภาษาอังกฤษ · <c>tools/dto_nullable_contract_check.py</c>)</summary>
/// <param name="Code">รหัส (คู่ค้าส่งมาเป็น <c>depositKindCode</c>) — แถวที่ระบบสร้าง (seed) เปลี่ยนรหัสไม่ได้</param>
/// <param name="Name">ชื่อที่ผู้ใช้เห็น (ตรึงลงใบมัดจำตอนออก)</param>
/// <param name="Nature">ลักษณะเงิน — ตัวกำหนด VAT (ส่งเป็นชื่อ enum)</param>
/// <param name="VatTreatment">วิธีบันทึก · null = ตามค่าตั้งต้นบริษัท (ตั้งค่า → ภาษี)</param>
/// <param name="LiabilityAccountCode">บัญชีหนี้สินพักเงิน · ว่าง = ค่าตามระบบ</param>
/// <param name="ForfeitAccountCode">บัญชีรายได้ตอนริบเป็นค่าเสียหาย · ว่าง = บัญชีของเส้นริบเดิม</param>
/// <param name="PolicyReason">เหตุผลเมื่อเลือกเลื่อน VAT กับเงินที่เป็นส่วนหนึ่งของราคา (ว่าง = ปฏิเสธ)</param>
/// <param name="IsActive">null = คงเดิม (สร้างใหม่ = เปิดใช้)</param>
/// <param name="SortOrder">null = คงเดิม (สร้างใหม่ = ต่อท้าย)</param>
public record SaveDepositKindRequest(
    string? Code,
    string? Name,
    DepositNature? Nature,
    DepositVatTreatment? VatTreatment,
    string? LiabilityAccountCode = null,
    string? ForfeitAccountCode = null,
    string? PolicyReason = null,
    string? Description = null,
    bool? IsActive = null,
    int? SortOrder = null);

/// <summary>ประเภทเงินมัดจำ 1 แถว + ผลตัดสินของเซิร์ฟเวอร์ (<c>DepositPolicyResolver.ResolveKind</c>) — หน้าเว็บแสดงอย่างเดียว</summary>
/// <param name="EffectiveTreatment">โหมดที่ใช้จริงเมื่อเลือกประเภทนี้บนใบ (ตกชั้นไปค่าตั้งบริษัทเมื่อประเภทไม่ได้ตั้งโหมด)</param>
/// <param name="EffectiveTreatmentSource">โหมดมาจากชั้นไหน (DocumentKind = ตั้งบนประเภท · CompanySetting · BusinessTypeDefault)</param>
/// <param name="Label">ป้ายของโหมดที่ใช้จริง</param>
/// <param name="EffectiveLiabilityAccountCode">บัญชีหนี้สินที่ใช้จริง (null = ค่าเดิมของ AutoPost 21712)</param>
/// <param name="RequiresReason">คู่ลักษณะ×โหมดนี้ต้องมีเหตุผล (ผลของตัวตัดสิน — รวมกรณีโหมดตกจากค่าตั้งบริษัท)</param>
/// <param name="IsSystem">แถวที่ระบบสร้าง (seed) — เปลี่ยนรหัสไม่ได้</param>
/// <param name="CanBeDefault">ตั้งเป็นประเภทเริ่มต้นได้ (เงินประกันที่ต้องคืน = ไม่ได้ · <c>DepositKindCatalog.DefaultKindProblem</c>) — หน้าเว็บซ่อนปุ่ม</param>
public record DepositKindResponse(
    Guid Id,
    string Code,
    string Name,
    DepositNature Nature,
    string NatureLabel,
    DepositVatTreatment? VatTreatment,
    DepositVatTreatment EffectiveTreatment,
    DepositVatTreatmentSource EffectiveTreatmentSource,
    string Label,
    string? Warning,
    string? RuleCode,
    bool RequiresReason,
    bool IsDefault,
    bool IsActive,
    string? LiabilityAccountCode,
    string? EffectiveLiabilityAccountCode,
    string? ForfeitAccountCode,
    string? PolicyReason,
    string? Description,
    int SortOrder,
    bool IsSystem,
    bool CanBeDefault = true);

/// <summary>ค่าตั้งต้นบริษัทที่ประเภท "ไม่ได้ตั้งโหมด" ใช้ (ตั้งค่า → ภาษี → วิธีบันทึกเงินมัดจำ)</summary>
public record DepositCompanyTreatmentInfo(
    DepositVatTreatment? Setting, DepositVatTreatment Effective, string Label, DepositVatTreatmentSource Source, bool NeedsOwnerChoice);

/// <summary>GET /deposit-kinds — รายการ + ประเภทเริ่มต้น + ตัวเลือก/ตาราง (ลักษณะ × โหมด) สำหรับฟอร์ม (ห้ามมีสำเนากติกาใน JS)</summary>
public record DepositKindListResponse(
    List<DepositKindResponse> Items,
    Guid? DefaultKindId,
    IReadOnlyList<DepositNatureOption> Natures,
    IReadOnlyList<DepositVatTreatmentOption> Treatments,
    IReadOnlyList<DepositKindMatrixCell> Matrix,
    DepositCompanyTreatmentInfo CompanyTreatment,
    DepositSupplyNature Supply);

/// <summary>ผลบันทึก — แถวที่บันทึก + คำเตือน (บันทึกสำเร็จแต่ผู้ใช้ต้องเห็น)</summary>
public record DepositKindSaveResponse(DepositKindResponse Item, string? Warning, string? RuleCode);

/// <summary>ผลลบ — ประเภทที่มีใบ/ที่พักอ้างอยู่ ⇒ ปิดใช้แทน (<c>Deactivated=true</c>) พร้อมเหตุ</summary>
public record DepositKindDeleteResponse(Guid Id, bool Deleted, bool Deactivated, string Message);
