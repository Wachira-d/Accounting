using System.Globalization;
using System.Linq.Expressions;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ลักษณะของสิ่งที่ขาย — ตัดสินว่า tax point ของเงินมัดจำอยู่ใต้มาตราไหน
/// (ไม่ persist · คำนวณจาก <see cref="IndustryType"/> หรือผู้เรียกระบุเองเมื่อรู้ชัด เช่นโมดูลที่พัก)</summary>
public enum DepositSupplyNature
{
    /// <summary>ให้บริการ — §78/1: tax point = ได้รับชำระ</summary>
    Service,
    /// <summary>ขายสินค้า — §78: ส่งมอบ/โอนกรรมสิทธิ์/ได้รับชำระ/ออกใบกำกับ แล้วแต่อย่างใดเกิดก่อน</summary>
    Goods,
    /// <summary>ไม่ทราบ/ผสม (ทั่วไป · ร้านอาหาร · คาเฟ่ · อื่น ๆ) — ระบบไม่เดาแทน ให้เจ้าของเลือกเอง</summary>
    Unknown,
}

/// <summary>ค่าที่ตัดสินได้มาจากชั้นไหน — หน้าเว็บบอกผู้ใช้ตรง ๆ ว่า "ใครเป็นคนตั้ง"</summary>
public enum DepositVatTreatmentSource
{
    /// <summary>ตั้งทับเฉพาะช่องทางนี้ (ที่พักแห่งนี้)</summary>
    ChannelOverride,
    /// <summary>ค่าตั้งต้นของบริษัท (ตั้งค่า → ภาษี)</summary>
    CompanySetting,
    /// <summary>ยังไม่มีใครตั้ง → ใช้ค่าตามประเภทธุรกิจ</summary>
    BusinessTypeDefault,
    /// <summary>รอบ 194 — ประเภทเงินมัดจำที่เลือกบนใบ</summary>
    DocumentKind,
    /// <summary>รอบ 194 — ประเภทเงินมัดจำของช่องทาง (ที่พัก/หน้าจอง)</summary>
    ChannelKind,
    /// <summary>รอบ 194 — ประเภทเงินมัดจำเริ่มต้นของบริษัท</summary>
    CompanyDefaultKind,
}

/// <summary>รูปของใบมัดจำที่ต้องส่งเข้า <c>CreateDocumentRequest</c> ให้ได้ JE ตามโหมด
/// (DocumentService ตัดสิน JE จากช่องเหล่านี้อยู่แล้ว — ไม่มีเส้นใหม่)</summary>
/// <param name="LineVatRate">อัตรา VAT ของบรรทัดใบมัดจำ (0 = ไม่แยก VAT)</param>
/// <param name="DepositOutputVatDeferred">ธงเดิมของเอกสาร: true = ขา VAT ลง 21913 / ไม่เข้า ภ.พ.30 จนรับรู้</param>
public readonly record struct DepositDocumentShape(decimal LineVatRate, bool DepositOutputVatDeferred);

/// <summary>ยอด JE ของใบมัดจำ (ราคารวม VAT) ตามโหมด — ใช้อธิบาย/ทดสอบ ตัวจริงคือ AutoPost ของ DocumentService</summary>
/// <param name="Liability">Cr มัดจำรับ/ขายรอรับรู้ (217xx)</param>
/// <param name="OutputVatDue">Cr ภาษีขาย 21911 (เข้า ภ.พ.30 เดือนที่รับเงิน)</param>
/// <param name="OutputVatUndue">Cr ภาษีขายรอเรียกเก็บ 21913 (ยังไม่เข้า ภ.พ.30)</param>
public readonly record struct DepositPostingPreview(decimal Gross, decimal Liability, decimal OutputVatDue, decimal OutputVatUndue);

/// <summary>ผลตัดสิน + ข้อความที่หน้าเว็บเอาไปแสดงได้เลย (Server computes · page displays — CLAUDE.md F2 ข้อ 5)</summary>
public sealed record DepositVatTreatmentDecision(
    DepositVatTreatment Treatment,
    DepositVatTreatmentSource Source,
    DepositSupplyNature Nature,
    bool NeedsOwnerChoice,
    string Label,
    string Explanation,
    string? Warning,
    string? WarningRuleCode);

/// <summary>ตัวเลือก 1 ข้อสำหรับหน้าเว็บ (ชื่อ enum + ป้าย + คำอธิบาย + มาตรา + คำเตือนที่จะขึ้นถ้าเลือก)
/// — หน้าเว็บเลือกแสดง <c>ServiceWarning</c> หรือ <c>OtherWarning</c> ตาม <c>Nature</c> ที่เซิร์ฟเวอร์ส่งมา (ไม่คิดเอง)</summary>
public sealed record DepositVatTreatmentOption(
    string Value, string Label, string Description, string LegalReference, string? ServiceWarning, string? OtherWarning);

/// <summary>ผลตัดสินประเภทเงินมัดจำ (รอบ 194 · spec S4) — ทุกทางเข้าที่ออกใบมัดจำใช้ record นี้ตัวเดียว
/// (<see cref="DepositDocumentShaping.Apply"/> · หมายเหตุบนใบ · ด่านหัก/ริบ)</summary>
/// <param name="KindId">ประเภทที่ถูกเลือก (null = ไม่มีประเภท — ตัดสินจากค่าเดิมของช่องทาง/บริษัท/ประเภทธุรกิจ)</param>
/// <param name="Code">รหัสประเภท (null เมื่อไม่มีประเภท)</param>
/// <param name="Name">ชื่อที่ตรึงลงใบ (<c>Document.DepositKindName</c>)</param>
/// <param name="Treatment">วิธีบันทึกที่ใช้จริง</param>
/// <param name="Nature">ลักษณะเงิน (ไม่มีประเภท = <see cref="DepositNature.PartOfPrice"/> ตามพฤติกรรมเดิม)</param>
/// <param name="LiabilityAccountCode">บัญชีหนี้สิน — ประเภทระบุเองชนะ · เงินประกันที่ไม่ระบุ = 21530/21620 · null = ค่าเดิมของทางเข้า (217xx)</param>
/// <param name="Source">ชั้นที่ตัดสิน<b>โหมด</b> (ประเภทที่ไม่ตั้งโหมด = ตกไปชั้นค่าเดิมของช่องทาง/ค่าตั้งบริษัท/ประเภทธุรกิจ)</param>
/// <param name="Warning">คำเตือนที่หน้าเว็บ/ใบ/ตอนอนุมัติแสดงได้เลย</param>
/// <param name="RuleCode">รหัสกฎของคำเตือน (RD-78/1 · RD-78(1)(b) · RD-PO73-SEC · RD-81)</param>
/// <param name="RequiresReason">ลักษณะ "ราคา" + โหมดเลื่อน VAT ⇒ ประเภทต้องมี <c>PolicyReason</c> (ด่านตอนบันทึกประเภท)</param>
/// <param name="NeedsOwnerChoice">ยังไม่มีใครตั้ง + ประเภทธุรกิจบอกลักษณะสิ่งที่ขายไม่ได้ ⇒ หน้าตั้งค่าแสดงเด่น</param>
/// <param name="Supply">ลักษณะสิ่งที่ขาย (สินค้า/บริการ/ไม่ทราบ) ที่ใช้เลือกรหัสกฎ</param>
/// <param name="ForfeitAccountCode">บัญชีรายได้ตอนริบเป็นค่าเสียหาย (null = บัญชีของเส้นริบเดิม)</param>
/// <param name="PolicyReason">เหตุผลที่บันทึกไว้บนประเภท (เก็บบนใบเป็นบันทึกภายใน <c>DepositPolicyNote</c> + คำเตือนตอนอนุมัติ — ไม่พิมพ์บนเอกสาร)</param>
public sealed record DepositKindDecision(
    Guid? KindId,
    string? Code,
    string Name,
    DepositVatTreatment Treatment,
    DepositNature Nature,
    string? LiabilityAccountCode,
    DepositVatTreatmentSource Source,
    string? Warning,
    string? RuleCode,
    bool RequiresReason,
    bool NeedsOwnerChoice,
    DepositSupplyNature Supply,
    string? ForfeitAccountCode,
    string? PolicyReason);

/// <summary>ผลของการริบมัดจำต่อ VAT (รอบ 194 · spec S3)</summary>
public enum DepositForfeitVatAction
{
    /// <summary>VAT เสียไปแล้วเดือนที่รับเงิน (ใบกำกับ/ย้ายเข้า 21911 แล้ว) ⇒ คงเดิม ไม่ออกใบลดหนี้ — พฤติกรรมเดิม</summary>
    KeepExistingVat = 1,
    /// <summary>VAT พักอยู่ 21913 ⇒ ย้ายเข้า 21911 (เส้นเดิม) + ธง <c>[DEPOSIT-LATE-VAT]</c></summary>
    ReclassifyUndueToDue = 2,
    /// <summary>ยังไม่เคยเสีย VAT (มัดจำเต็มยอด) ⇒ ออกใบกำกับภาษีของยอดที่ริบ (ราคารวม VAT) แล้วตัดชำระด้วยมัดจำ
    /// (<c>ApplyDepositToInvoiceCoreAsync</c>) + ธง <c>[DEPOSIT-LATE-VAT]</c> — ห้ามลงรายได้ไม่มี VAT เงียบ ๆ</summary>
    IssueTaxInvoiceForForfeit = 3,
    /// <summary>ค่าเสียหายแท้ ⇒ รายได้อื่นไม่มี VAT (บัญชี <c>ForfeitAccountCode</c>)</summary>
    CompensationNoVat = 4,
    /// <summary>สิ่งที่ขายอยู่นอกระบบ VAT ⇒ รายได้ไม่มี VAT</summary>
    NonVatNoVat = 5,
    /// <summary>บริษัทไม่จด VAT ⇒ รายได้ไม่มี VAT (ไม่มีใบกำกับ)</summary>
    CompanyNotVatRegistered = 6,
    /// <summary>รอบ 194 M2 — ใบมัดจำออกด้วย VAT 0 <b>โดยชอบ</b> (ไม่ใช่การเลื่อน VAT: ยกเว้น §81 · อัตรา 0% §80/1 · ออกตอนยังไม่จด VAT)
    /// ⇒ ริบแล้วคงอัตราเดิม (ไม่มี VAT · ไม่ออกใบกำกับ 7% · ไม่ติดธงภาษีย้อนหลัง) · รอบ 194 R2-2: รู้ได้เฉพาะเมื่อ ① ใบมีลักษณะเงิน
    /// (ใบรอบ 194+ ที่ธงเลื่อนเชื่อถือได้) และธงเลื่อน = false · ② ช่องทางบอกอัตรา 0 (ที่พัก <c>ChargeVat=false</c>) ·
    /// ③ ผู้ใช้ยืนยัน <see cref="DepositForfeitAs.OriginallyNoVat"/> กับใบเดิมที่กำกวม — ดู <see cref="DepositPolicyResolver.ForfeitZeroVatDeferred"/></summary>
    ZeroVatAtIssue = 7,
}

/// <summary>รอบ 194 M4 — จุดความรับผิดของ VAT ที่เกิดจากการริบ (ใบกำกับของยอดที่ริบ / ย้าย 21913 → 21911)</summary>
/// <param name="TaxPointDate">วันที่ที่ใช้เป็น tax point (งวด ภ.พ.30) — ใบกำกับส่งเป็น <c>PaymentDate</c> · ใบ VAT พักประทับ <c>DepositOutputVatRecognizedAt</c></param>
/// <param name="LateFlag">ต้องติดธง <c>[DEPOSIT-LATE-VAT]</c> (งวดเดือนรับเงินอาจยื่นไปแล้ว — ยื่น/ล็อกในระบบ · เลยกำหนดยื่น · ข้ามปีภาษี
/// ⇒ VAT เข้างวดปัจจุบันแบบล่าช้า)</param>
/// <param name="Note">ข้อความที่ประทับบนใบ (มีธงเมื่อ <paramref name="LateFlag"/>) — null = ไม่มีอะไรต้องบอก</param>
public sealed record DepositForfeitTaxPoint(DateTime TaxPointDate, bool LateFlag, string? Note);

/// <summary>รอบ 194 P-e — ใบมัดจำที่ "เลขอ้างอิง" ชี้ถึง (ต้องเป็นใบที่ออกแล้ว ไม่ยกเลิก — ผู้เรียกกรองก่อน)</summary>
public sealed record DepositRefCandidate(Guid Id, string DocumentNumber, string? Reference, DepositNature? Nature);

/// <summary>รอบ 194 M5/P-c — บัญชีรายได้ของการรับรู้/ริบมัดจำ: ลำดับรหัสที่ลอง + ไม่พบเลยต้องล้มดังไหม</summary>
/// <param name="Codes">รหัสที่ลองตามลำดับ (ว่าง/ซ้ำถูกตัดแล้ว)</param>
/// <param name="FailIfNone">true = ไม่พบบัญชีใดเลย ⇒ ล้มดังพร้อมทางไปต่อ (ห้ามตกไปรายได้ขาย) · false = ตกไปบัญชีรายได้ใดก็ได้ตามเดิม</param>
/// <param name="IsForfeit">รายการนี้คือ "ริบ" (คำอธิบาย JE ต้องบอกตามจริง)</param>
public sealed record DepositRevenueAccountPlan(IReadOnlyList<string> Codes, bool FailIfNone, bool IsForfeit);

/// <summary>ผลตัดสิน VAT ของการริบ</summary>
/// <param name="EffectiveAs">ถือว่าเป็นอะไรจริง (หลังใช้ลักษณะเงินที่รู้แล้ว/ค่าเริ่มต้นทิศปลอดภัย)</param>
/// <param name="LateVat">ต้องติดธง <c>[DEPOSIT-LATE-VAT]</c> (ภาษีถึงกำหนดตั้งแต่เดือนที่รับเงิน — ยื่น ภ.พ.30 เพิ่มเติม)</param>
/// <param name="ReverseUndueVat">VAT พัก 21913 แล้วริบเป็นค่าเสียหาย ⇒ ต้องกลับ 21913 เข้ารายได้ (ไม่ใช่ย้ายเข้า 21911)</param>
/// <param name="RequestIgnored">ขอ "ค่าเสียหาย" กับเงินที่ลักษณะเป็นราคา ⇒ ไม่มีผล (หน้าจอต้องบอก — ห้าม silent no-op)</param>
/// <param name="ForfeitInvoiceVatRate">อัตรา VAT ของใบกำกับยอดที่ริบ (มีค่าเฉพาะ <see cref="DepositForfeitVatAction.IssueTaxInvoiceForForfeit"/>)</param>
/// <remarks>รอบ 194 M4: ข้อความธงไม่อยู่ในผลนี้แล้ว — เดิมสั่ง "ยื่น ภ.พ.30 เพิ่มเติมของเดือนนั้น" ทั้งที่ใบกำกับเข้ารายงานเดือนที่ริบ
/// (ผู้ใช้ทำตามทั้งสองทาง = VAT ซ้ำ) · ข้อความจริงขึ้นกับ "งวดเดือนรับเงินยื่นแล้วหรือยัง" ⇒
/// <see cref="DepositPolicyResolver.ForfeitTaxPointDecision"/> ตัวเดียว</remarks>
public sealed record DepositForfeitVatDecision(
    DepositForfeitVatAction Action,
    DepositForfeitAs EffectiveAs,
    bool LateVat,
    bool ReverseUndueVat,
    bool RequestIgnored,
    decimal ForfeitInvoiceVatRate,
    string Explanation,
    string? RuleCode);

/// <summary>
/// **ตัวตัดสินตัวเดียว** ของ "เงินมัดจำฝั่งขายบันทึกแบบไหน" (รอบ 193 · คำตัดสินเจ้าของ #34 + คำชี้แจงสองรอบ:
/// "ขึ้นอยู่กับประเภทธุรกิจ ควรตั้งค่าได้อย่างชัดเจน" และ "รับเป็นมัดจำเต็มยอด / หักภาษีรอนำส่งไว้ก่อน /
/// หักภาษีเลย … ต้องตั้งค่าได้ทั้งหมด")
///
/// ═══ ลำดับชั้น ═══  ช่องทางตั้งทับ (ที่พัก) → ค่าตั้งต้นบริษัท → ค่าตามประเภทธุรกิจ
///
/// ═══ ค่าตามประเภทธุรกิจ (เมื่อยังไม่มีใครตั้ง) ═══
/// ทุกประเภท = <see cref="DepositVatTreatment.VatImmediate"/> — สำหรับ<b>บริการ</b>นี่คือกฎหมาย (§78/1) ·
/// สำหรับ<b>สินค้า/ไม่ทราบ</b>นี่คือ<b>พฤติกรรมเดิม</b>ของทุกทางเข้า (ฟอร์มเอกสาร radio ค่าเริ่มต้น "ถึงกำหนดแล้ว" ·
/// ที่พัก <c>DepositOutputVatDeferred=false</c> · CMS booking · integration default false) ⇒ ไม่เปลี่ยนพฤติกรรมของใคร
/// เงียบ ๆ · ธุรกิจที่บอกประเภทไม่ได้ได้ธง <c>NeedsOwnerChoice</c> ให้หน้าตั้งค่าแสดงเด่น
///
/// ⚠️ มีผลกับ "มัดจำใบใหม่" เท่านั้น — ใบที่ออกแล้วไม่ถูกเขียนย้อน (§86/4)
///
/// ═══ owner file ═══ ตรงกับข้อเสนอของทีมตรวจวงจรมัดจำ (<c>erp-review/2026-09-24/audit-deposit.md</c> §6
/// "Helpers/DepositPolicyResolver ตัวเดียว") — ชื่อโหมดใช้ตามที่เจ้าของระบุ (FullDeposit ≙ NoVatUntilFinal ·
/// VatPendingUndue ≙ VatUndue · VatImmediate) · "วิธีหักที่ใบสุดท้าย" ไม่ใช่ค่าตั้งแยก แต่<b>ตามมาจากโหมดของใบมัดจำ</b>
/// (VatImmediate ⇒ หักฐานก่อนคิด VAT · อีกสองโหมด ⇒ ใบเต็ม + ตัดชำระด้วยมัดจำ) จึงไม่มีคู่ที่ผิดกฎหมายให้เลือกได้ ·
/// โหมดของใบที่ออกแล้วอ่านย้อนจากช่องที่ตรึงบนใบ (<see cref="OfDocument"/>) ไม่อ่านค่าบริษัทซ้ำ ·
/// แผนตัวเลขของโมดูลที่พัก (หัก/ริบ/คืน) อยู่ที่ <c>Helpers/LodgingDepositSettlement</c> ซึ่งกินผลของไฟล์นี้
/// </summary>
public static class DepositPolicyResolver
{
    /// <summary>รหัสกฎของด่าน "ห้ามหักมัดจำที่ออกใบกำกับแล้วแบบเต็มจำนวนเข้าใบกำกับที่คิด VAT เต็ม" (P0-3 รอบ 193)</summary>
    public const string ImmediateVatGrossApplyRuleCode = "RD-86/4-DEPOSIT-TIV-DOUBLE";
    public const string ServiceNonImmediateRuleCode = "RD-78/1-DEPOSIT-VAT";
    public const string GoodsNonImmediateRuleCode = "RD-78-DEPOSIT-VAT";

    public static string LabelOf(DepositVatTreatment t) => t switch
    {
        DepositVatTreatment.FullDeposit => "รับเป็นเงินมัดจำเต็มยอด ไม่แยก VAT",
        DepositVatTreatment.VatPendingUndue => "แยก VAT เป็นภาษีขายรอเรียกเก็บ (ยังไม่ถึงกำหนด)",
        _ => "ออกใบกำกับภาษี รับรู้ VAT ทันทีที่รับเงิน",
    };

    private static string DescriptionOf(DepositVatTreatment t) => t switch
    {
        DepositVatTreatment.FullDeposit =>
            "ใบมัดจำเป็น \"ใบเสร็จรับเงิน\" ไม่มี VAT — ลงเงินมัดจำรับ (หนี้สิน) เต็มยอด · ภาษีขายเกิดครั้งเดียวที่ใบกำกับใบสุดท้าย"
            + "เต็มราคา · เหมาะกับเงินประกันที่ต้องคืน (ยังไม่เกิดจุดความรับผิด) · ริบมัดจำ: ถ้าเป็นส่วนหนึ่งของราคา/ค่าธรรมเนียมยกเลิก "
            + "ต้องออกใบกำกับภาษีของยอดที่ริบ (ภาษีถึงกำหนดตั้งแต่เดือนที่รับเงิน) · ไม่มี VAT เฉพาะเมื่อเป็นค่าเสียหายของเงินประกันแท้",
        DepositVatTreatment.VatPendingUndue =>
            "ใบมัดจำเป็น \"ใบเสร็จรับเงิน\" แยกฐาน + ภาษีขายรอเรียกเก็บ (21913 · ยังไม่เข้า ภ.พ.30) — ย้ายเป็นภาษีขาย (21911) "
            + "เมื่อออกใบสุดท้าย/ริบมัดจำ · ระบบเตือนใบที่ค้างเกิน 90 วันตอนเปิดรายงาน ภ.พ.30",
        _ =>
            "ใบมัดจำเป็น \"ใบกำกับภาษี/ใบเสร็จรับเงิน\" (เลขชุดใบกำกับ) ภาษีขาย (21911) เข้า ภ.พ.30 เดือนที่รับเงิน — "
            + "ใบสุดท้ายหักมูลค่ามัดจำ (ก่อน VAT) ออกจากฐานภาษี จึงไม่นับ VAT ซ้ำ และผู้ซื้อไม่เคลมภาษีซื้อซ้ำ",
    };

    private static string LegalReferenceOf(DepositVatTreatment t) => t switch
    {
        DepositVatTreatment.VatImmediate => "ป.รัษฎากร มาตรา 78/1(1) (บริการ: ได้รับชำระ) · มาตรา 78(1)(ข) (สินค้า: ได้รับชำระราคาก่อนส่งมอบ) · "
            + "คำสั่งกรมสรรพากรที่ ป.73/2541 (เงินมัดจำ/เงินจอง/ชำระล่วงหน้าที่เป็นส่วนหนึ่งของราคา) · มาตรา 86/4",
        _ => "ป.รัษฎากร มาตรา 78(1)(ข) / 78/1(1) + คำสั่งกรมสรรพากรที่ ป.73/2541 — ใช้ได้เมื่อยังไม่เกิดจุดความรับผิด "
            + "(เงินประกันที่ต้องคืนซึ่งยังไม่ใช่ค่าตอบแทน) · มัดจำที่เป็นส่วนหนึ่งของราคาภาษีถึงกำหนดตอนรับเงิน",
    };

    /// <summary>ตัวเลือกทั้งหมดเรียงตามที่เจ้าของระบุ (หน้าเว็บสร้าง radio จากลิสต์นี้ — ห้ามพิมพ์ซ้ำใน JS)</summary>
    public static IReadOnlyList<DepositVatTreatmentOption> Options { get; } =
        new[] { DepositVatTreatment.FullDeposit, DepositVatTreatment.VatPendingUndue, DepositVatTreatment.VatImmediate }
            .Select(t => new DepositVatTreatmentOption(t.ToString(), LabelOf(t), DescriptionOf(t), LegalReferenceOf(t),
                WarningFor(DepositSupplyNature.Service, t).Warning, WarningFor(DepositSupplyNature.Goods, t).Warning))
            .ToArray();

    /// <summary>คำอธิบายหนึ่งย่อหน้าใต้หัวข้อการตั้งค่า</summary>
    public const string Explanation =
        "มัดจำที่เป็น \"ส่วนหนึ่งของราคา\" (เงินจอง/ดาวน์/ชำระล่วงหน้า) ทำให้ภาษีขายถึงกำหนดตั้งแต่วันที่ได้รับเงิน: "
        + "ธุรกิจบริการ ป.รัษฎากร มาตรา 78/1 ถือวันที่ได้รับชำระค่าบริการเป็นจุดความรับผิด (tax point) · การขายสินค้า มาตรา 78 "
        + "ถือวันส่งมอบ โอนกรรมสิทธิ์ ได้รับชำระ หรือออกใบกำกับ แล้วแต่อย่างใดเกิดก่อน — รับเงินก่อนส่งมอบจึงถึงกำหนดตอนรับเงินเช่นกัน · "
        + "\"มัดจำเต็มยอด\" และ \"ภาษีรอเรียกเก็บ\" ถูกต้องเฉพาะเงินประกัน/มัดจำที่ต้องคืนซึ่งยังไม่ใช่ค่าตอบแทน · "
        + "การตั้งค่านี้ใช้กับมัดจำใบใหม่เท่านั้น ใบที่ออกไปแล้วไม่ถูกแก้ย้อนหลัง";

    /// <summary>ประเภทธุรกิจ → ลักษณะสิ่งที่ขาย · กลุ่มที่บอกไม่ได้ = <see cref="DepositSupplyNature.Unknown"/>
    /// (DECISION_DOCTRINE §1 — "ไม่รู้" ต้องเป็นค่าใน enum ห้ามเดาแทน)</summary>
    public static DepositSupplyNature NatureOf(IndustryType industry) => industry switch
    {
        // RealEstate ไม่อยู่ที่นี่ (รอบ 194 · spec S4 · L2 §7.2): อาจเป็นให้เช่า (ยกเว้น §81(1)(ต)) · ขาย (SBT §81(1)(น)) ·
        // นายหน้า/ส่วนกลาง (บริการ มี VAT) — เดิมเดาเป็นบริการ ⇒ ค่าแนะนำคิด VAT 7% กับค่าเช่าที่ยกเว้น ⇒ ให้เจ้าของเลือก
        IndustryType.Service or IndustryType.Hotel or IndustryType.Construction
            or IndustryType.Technology or IndustryType.Healthcare or IndustryType.Education
            or IndustryType.Beauty or IndustryType.Transportation or IndustryType.Freelance
            => DepositSupplyNature.Service,
        IndustryType.Trading or IndustryType.Manufacturing or IndustryType.Retail
            or IndustryType.Agriculture or IndustryType.Ecommerce
            => DepositSupplyNature.Goods,
        _ => DepositSupplyNature.Unknown,
    };

    /// <summary>ค่าที่รับจาก client/DB เป็นค่าที่นิยามไว้จริงไหม (กันเลขขยะ เช่น 0 จาก select ว่าง)</summary>
    public static bool IsDefined(DepositVatTreatment? t) => t is DepositVatTreatment v && Enum.IsDefined(v);

    /// <summary>ตัวตัดสิน — pure</summary>
    /// <param name="nature">ลักษณะสิ่งที่ขาย (โมดูลที่พักส่ง <see cref="DepositSupplyNature.Service"/> เสมอ — ค่าห้องพักเป็นบริการโดยสภาพ)</param>
    /// <param name="companySetting">ค่าตั้งต้นบริษัท (null = ยังไม่เคยตั้ง)</param>
    /// <param name="channelOverride">ตั้งทับรายช่องทาง (ที่พัก) — null = ตามบริษัท</param>
    public static DepositVatTreatmentDecision Resolve(
        DepositSupplyNature nature, DepositVatTreatment? companySetting, DepositVatTreatment? channelOverride = null)
    {
        DepositVatTreatment t;
        DepositVatTreatmentSource source;
        if (IsDefined(channelOverride)) { t = channelOverride!.Value; source = DepositVatTreatmentSource.ChannelOverride; }
        else if (IsDefined(companySetting)) { t = companySetting!.Value; source = DepositVatTreatmentSource.CompanySetting; }
        else { t = DepositVatTreatment.VatImmediate; source = DepositVatTreatmentSource.BusinessTypeDefault; }

        var needsChoice = source == DepositVatTreatmentSource.BusinessTypeDefault && nature == DepositSupplyNature.Unknown;
        var (warning, code) = WarningFor(nature, t);
        return new DepositVatTreatmentDecision(t, source, nature, needsChoice, LabelOf(t), Explanation, warning, code);
    }

    /// <summary>คำเตือนเมื่อเลือกโหมดที่ไม่ใช่ "รับรู้ทันที" — บริการ = ขัด §78/1 (เตือนแรง) · สินค้า/ไม่ทราบ = แจ้งให้ทราบ</summary>
    private static (string? Warning, string? RuleCode) WarningFor(DepositSupplyNature nature, DepositVatTreatment t)
    {
        if (t == DepositVatTreatment.VatImmediate) return (null, null);
        var what = t == DepositVatTreatment.FullDeposit ? "\"มัดจำเต็มยอด ไม่แยก VAT\"" : "\"ภาษีรอเรียกเก็บ\"";
        return nature == DepositSupplyNature.Service
            ? ($"⚠️ ธุรกิจบริการเลือก {what} — ขัด ป.รัษฎากร มาตรา 78/1: ค่าบริการที่ได้รับชำระแล้ว (รวมมัดจำ/เงินจอง) "
               + "ภาษีขายถึงกำหนดทันทีในเดือนที่รับเงิน · เลื่อนไปเดือนที่ออกใบสุดท้าย = นำส่งภาษีช้า เสี่ยงเงินเพิ่ม 1.5%/เดือน "
               + "(มาตรา 89/1) และเบี้ยปรับ · ใช้ได้ถูกต้องเฉพาะเงินประกันความเสียหาย/มัดจำที่ต้องคืนซึ่งไม่ใช่ค่าตอบแทน",
               ServiceNonImmediateRuleCode)
            // รอบ 194 (spec S2 · L2 §8): สินค้าเตือนแรงเท่าบริการ — เดิมเป็น ℹ️ ทั้งที่ §78(1)(ข) ให้ภาษีถึงกำหนดตอนรับเงินเช่นกัน
            : ($"⚠️ เลือก {what} — มัดจำ/เงินดาวน์ค่าสินค้าที่รับก่อนส่งมอบ ภาษีขายถึงกำหนดตอนได้รับชำระ (ป.รัษฎากร มาตรา 78(1)(ข) · "
               + "ป.73/2541) · เลื่อนไปเดือนที่ออกใบสุดท้าย = นำส่งภาษีช้า เสี่ยงเงินเพิ่ม 1.5%/เดือน (มาตรา 89/1) และเบี้ยปรับ · "
               + "ใช้ได้ถูกต้องเฉพาะเงินประกัน/มัดจำที่ต้องคืนซึ่งยังไม่ใช่ส่วนหนึ่งของราคา",
               GoodsNonImmediateRuleCode);
    }

    /// <summary>โหมด → รูปของใบมัดจำที่ส่งเข้า DocumentService (บริษัทไม่คิด VAT ⇒ ไม่มี VAT ทุกโหมด · พฤติกรรมเดิม)</summary>
    public static DepositDocumentShape ShapeFor(DepositVatTreatment t, decimal vatRate)
    {
        if (vatRate <= 0m) return new DepositDocumentShape(0m, false);
        return t switch
        {
            // VAT 0 + ธง deferred ⇒ TaxService ไม่นับเป็นยอดขายยกเว้นภาษีในรายงานภาษีขาย (มัดจำยังเป็นหนี้สิน)
            DepositVatTreatment.FullDeposit => new DepositDocumentShape(0m, true),
            DepositVatTreatment.VatPendingUndue => new DepositDocumentShape(vatRate, true),
            _ => new DepositDocumentShape(vatRate, false),
        };
    }

    /// <summary>อ่านโหมดย้อนจากใบมัดจำที่ออกแล้ว — ใช้ช่องที่ตรึงตอนสร้าง (ไม่เปลี่ยนหลังอนุมัติ) · null = ไม่ใช่ใบมัดจำ</summary>
    public static DepositVatTreatment? OfDocument(bool isDeposit, decimal vatAmount, bool depositOutputVatDeferred)
        => !isDeposit ? null
         : vatAmount <= 0.005m ? DepositVatTreatment.FullDeposit
         : depositOutputVatDeferred ? DepositVatTreatment.VatPendingUndue
         : DepositVatTreatment.VatImmediate;

    /// <summary>ข้อความอธิบายยอด JE ของใบมัดจำ (ใส่หมายเหตุภายในของใบ — ให้คนตรวจเห็นว่าโหมดนี้ลงบัญชีอย่างไร)</summary>
    public static string DescribePosting(DepositVatTreatment t, decimal gross, decimal vatRate)
    {
        var p = PreviewReceipt(t, gross, vatRate);
        return $"รับ {p.Gross:N2} → มัดจำรับ {p.Liability:N2}"
            + (p.OutputVatDue > 0 ? $" + ภาษีขาย {p.OutputVatDue:N2} (21911 · ภ.พ.30 เดือนนี้)" : "")
            + (p.OutputVatUndue > 0 ? $" + ภาษีขายรอเรียกเก็บ {p.OutputVatUndue:N2} (21913)" : "");
    }

    /// <summary>ยอด JE ของใบมัดจำราคารวม VAT ตามโหมด (1,000 @7%: เต็มยอด 1,000 · รอเรียกเก็บ 934.58 + 65.42 (21913) ·
    /// ทันที 934.58 + 65.42 (21911))</summary>
    public static DepositPostingPreview PreviewReceipt(DepositVatTreatment t, decimal gross, decimal vatRate)
    {
        var shape = ShapeFor(t, vatRate);
        if (shape.LineVatRate <= 0m) return new DepositPostingPreview(gross, gross, 0m, 0m);
        var (b, v) = LodgingDepositSettlement.SplitInclusive(gross, shape.LineVatRate);
        return shape.DepositOutputVatDeferred
            ? new DepositPostingPreview(gross, b, 0m, v)
            : new DepositPostingPreview(gross, b, v, 0m);
    }

    /// <summary>ธงบนเอกสารจากระบบต้นทาง (TakeTime) ที่จังหวะ VAT ของมัดจำขัดกับการตั้งค่าบริษัท —
    /// <b>ไม่แก้ยอดที่คู่ค้าคำนวณ</b> (เรารู้แค่ธง ไม่รู้ว่าเขายื่นภาษีไปแล้วอย่างไร) แค่ทิ้งร่องรอยให้นักบัญชีเห็น · null = สอดคล้อง</summary>
    /// <param name="payloadDeferred">ธง <c>DepositOutputVatDeferred</c> ที่ payload ส่งมา — เทียบเฉพาะ "VAT ของมัดจำ
    /// รับรู้แล้วหรือยัง" (payload ไม่บอกว่ามัดจำแยก VAT หรือเต็มยอด จึงไม่เทียบละเอียดกว่านั้น — กันธงเท็จ)</param>
    public static string? IntegrationMismatchNote(bool payloadDeferred, DepositVatTreatmentDecision company)
    {
        var companyDeferred = company.Treatment != DepositVatTreatment.VatImmediate;
        if (payloadDeferred == companyDeferred) return null;
        var code = !payloadDeferred ? "DEPOSIT-VAT-TREATMENT"
            : company.Nature == DepositSupplyNature.Service ? ServiceNonImmediateRuleCode : GoodsNonImmediateRuleCode;
        var payloadText = payloadDeferred ? "VAT ของมัดจำยังพักรอ (ยังไม่เข้า ภ.พ.30)" : "VAT ของมัดจำรับรู้แล้วตอนรับเงิน (21911)";
        return $"[{code}] ระบบต้นทางแจ้งว่า{payloadText} แต่การตั้งค่าบริษัทคือ \"{LabelOf(company.Treatment)}\" "
            + "— ระบบบันทึกตามข้อมูลต้นทาง ไม่แก้ยอดที่คู่ค้าคำนวณ · ให้นักบัญชีตรวจว่า ภ.พ.30 เดือนที่รับมัดจำถูกต้องหรือต้องยื่นเพิ่มเติม"
            + (company.Nature == DepositSupplyNature.Service && payloadDeferred ? " (บริการ: tax point = วันรับเงิน §78/1)" : "");
    }

    /// <summary>รอบ 194 (spec S5 · ทีม C): ธงเดียวกับ <see cref="IntegrationMismatchNote(bool, DepositVatTreatmentDecision)"/> แต่คำนวณจาก
    /// ผลตัดสินประเภท (<see cref="ResolveKind"/>) — "VAT ของมัดจำยังพักรอ" ตามประเภท = โหมดไม่ใช่ VAT ทันที หรือ นอกระบบ VAT
    /// (รูปใบเดียวกับมัดจำเต็มยอด) · รหัสกฎ: คู่ค้าบอกว่าพักรอแต่ประเภทเป็น "ส่วนหนึ่งของราคา" + VAT ทันที ⇒ รหัส §78/1 / §78 เดิม ·
    /// อย่างอื่นที่ขัดกัน ⇒ <c>DEPOSIT-VAT-TREATMENT</c> · ไม่มีประเภท/ประเภทไม่ได้ตั้งโหมด ⇒ ข้อความเดิมทุกตัวอักษร ("การตั้งค่าบริษัทคือ")</summary>
    public static string? IntegrationMismatchNote(bool payloadDeferred, DepositKindDecision kind)
    {
        var kindDeferred = kind.Nature == DepositNature.NonVatSupply || kind.Treatment != DepositVatTreatment.VatImmediate;
        if (payloadDeferred == kindDeferred) return null;
        var code = payloadDeferred && kind.Nature == DepositNature.PartOfPrice
            ? (kind.Supply == DepositSupplyNature.Service ? ServiceNonImmediateRuleCode : GoodsNonImmediateRuleCode)
            : "DEPOSIT-VAT-TREATMENT";
        var payloadText = payloadDeferred ? "VAT ของมัดจำยังพักรอ (ยังไม่เข้า ภ.พ.30)" : "VAT ของมัดจำรับรู้แล้วตอนรับเงิน (21911)";
        var modeText = kind.Nature == DepositNature.NonVatSupply ? "นอกระบบ VAT (VAT 0)" : LabelOf(kind.Treatment);
        var settingText = kind.Source switch
        {
            DepositVatTreatmentSource.DocumentKind => $"ประเภทเงินมัดจำ “{kind.Name}” ที่ระบบต้นทางระบุ บันทึกแบบ \"{modeText}\"",
            DepositVatTreatmentSource.CompanyDefaultKind => $"ประเภทเงินมัดจำเริ่มต้นของบริษัท “{kind.Name}” บันทึกแบบ \"{modeText}\"",
            _ => $"การตั้งค่าบริษัทคือ \"{modeText}\"",
        };
        return $"[{code}] ระบบต้นทางแจ้งว่า{payloadText} แต่{settingText} "
            + "— ระบบบันทึกตามข้อมูลต้นทาง ไม่แก้ยอดที่คู่ค้าคำนวณ · ให้นักบัญชีตรวจว่า ภ.พ.30 เดือนที่รับมัดจำถูกต้องหรือต้องยื่นเพิ่มเติม"
            + (kind.Supply == DepositSupplyNature.Service && payloadDeferred && kind.Nature == DepositNature.PartOfPrice
                ? " (บริการ: tax point = วันรับเงิน §78/1)" : "");
    }

    /// <summary>ต่อข้อความธงเข้าหมายเหตุภายในแบบไม่ซ้ำ (resync ส่งใบเดิมมาหลายรอบได้)</summary>
    public static string? AppendNoteOnce(string? existing, string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return existing;
        if (!string.IsNullOrEmpty(existing) && existing.Contains(note, StringComparison.Ordinal)) return existing;
        return string.IsNullOrWhiteSpace(existing) ? note : existing.TrimEnd() + " · " + note;
    }

    /// <summary>ใบนี้ "หักมูลค่ามัดจำ (ก่อน VAT) ตามใบกำกับภาษี" หรือไม่ — ตัวตัดสินตัวเดียวของทั้งสอง renderer (HTML + QuestPDF)
    /// และของการรับรู้มัดจำตอนอนุมัติ (<c>RealizeTaxedDepositDeductionsAsync</c>)
    /// <para>รอบ 193 ฝ่ายค้านรอบสาม R3-1: ฐานมัดจำอยู่ช่องของตัวเอง (<c>Document.DepositBaseDeducted</c>) แยกจากส่วนลดการค้า
    /// (<c>BillDiscountAmount</c>) — เดิมตัดสินจาก <c>BillDiscountAmount</c> ⇒ ส่วนลด 100 บาทถูกพิมพ์และรับรู้เป็น "มัดจำ" ·
    /// ไม่ต้องดู <c>DepositAppliedAmount</c> อีก (ช่องใหม่มีแต่ฐานมัดจำออกใบกำกับ ⇒ ใบที่หักมัดจำ VAT พักเพิ่มภายหลังยังพิมพ์ถูก — B5)</para></summary>
    public static bool TaxedDepositDeducted(decimal depositBaseDeducted, string? depositAppliedRef)
        => depositBaseDeducted > 0m && !string.IsNullOrWhiteSpace(depositAppliedRef);

    /// <summary>ส่วนหักท้ายบิลทั้งหมดของใบ (ก่อน VAT) = ส่วนลดการค้า + ฐานมัดจำออกใบกำกับแล้ว — ยอดก่อนหักท้ายบิลบนกระดาษ =
    /// SubTotal + ค่านี้ · สัดส่วนที่ renderer ใช้ scale ยอดบรรทัดกลับ · ตัวเดียวของทั้งสอง renderer (R3-1 — ห้ามบวกเองที่ปลายทาง)</summary>
    public static decimal BillDeductionTotal(decimal billDiscountAmount, decimal depositBaseDeducted)
        => billDiscountAmount + depositBaseDeducted;

    /// <summary>ด่านของค่าที่จะบันทึกลง <c>DepositBaseDeducted</c> (สร้าง/แก้) — null = ผ่าน · ข้อความ = เหตุ + ทางไปต่อ
    /// (ผู้เรียกโยน <see cref="BusinessRuleException"/> พร้อม <see cref="ImmediateVatGrossApplyRuleCode"/>)</summary>
    public static string? TaxedDepositDeductionProblem(
        DocumentType documentType, bool? cnDnPurchaseSide,
        decimal depositBaseDeducted, string? depositAppliedRef, decimal depositAppliedAmount, bool drivesJournal, decimal billDiscountPercent)
    {
        if (depositBaseDeducted < 0m) return "ฐานมัดจำที่หักต้องไม่ติดลบ";
        if (depositBaseDeducted == 0m) return null;
        if (!TaxedDepositDeductionAllowed(documentType, cnDnPurchaseSide)) return DeductionOnPurchaseSideMessage;
        if (string.IsNullOrWhiteSpace(depositAppliedRef))
            return "หักมูลค่ามัดจำ (ก่อน VAT) ต้องระบุเลขใบมัดจำที่ออกใบกำกับแล้ว (depositAppliedRef) — ไม่มีเลขอ้างอิง ระบบรับรู้มัดจำตอนอนุมัติไม่ได้";
        if (depositAppliedAmount > 0m || drivesJournal)
            return "หักมูลค่ามัดจำ (ก่อน VAT) แล้ว ห้ามหักมัดจำแบบยอดรวม VAT ซ้ำในใบเดียวกัน — เลือกอย่างใดอย่างหนึ่ง";
        if (billDiscountPercent > 0m) return PercentWithTaxedDepositMessage;
        return null;
    }

    /// <summary>ชนิดเอกสารที่ "หักมูลค่ามัดจำ (ก่อน VAT) ตามใบกำกับภาษี" ใช้ได้ — <b>ฝั่งขายเท่านั้น</b> (มัดจำคือเงินที่ลูกค้าจ่ายเรา) ·
    /// ตัวตัดสินเดียวของด่านบันทึกและการรับรู้ตอนอนุมัติ (ฝ่ายค้านรอบสี่ P4-4: เดิมไม่จำกัดชนิด ⇒ API ตั้งบนใบแจ้งหนี้ซื้อแล้วอนุมัติ =
    /// รับรู้มัดจำขายเป็นรายได้) · ใบลดหนี้/เพิ่มหนี้/ใบส่งของ (สองฝั่ง) ได้เมื่อไม่ได้ระบุว่าเป็นฝั่งซื้อ (ใบลูกที่แปลงจากใบกำกับขายสืบทอดมา)</summary>
    public static bool TaxedDepositDeductionAllowed(DocumentType documentType, bool? cnDnPurchaseSide)
        => DocumentSide.IsAmbiguous(documentType) ? cnDnPurchaseSide != true : DocumentSide.IsSales(documentType);

    public const string DeductionOnPurchaseSideMessage =
        "หักมูลค่ามัดจำ (ก่อน VAT) ตามใบกำกับภาษี ใช้ได้กับเอกสารฝั่งขายเท่านั้น (มัดจำที่ลูกค้าจ่ายเรา) — เอกสารฝั่งซื้อ/ใบลดหนี้ฝั่งซื้อ "
        + "ห้ามตั้ง depositBaseDeducted · มัดจำที่เราจ่ายผู้ขายให้บันทึกเป็นเงินจ่ายล่วงหน้าตามปกติ";

    /// <summary>ส่วนลดท้ายบิลแบบ % ใช้ร่วมกับหักมัดจำออกใบกำกับแล้วไม่ได้ (ตัวเฉลี่ยใช้ % แล้วทิ้งยอดบาท ⇒ ฐานมัดจำหายเงียบ)</summary>
    public const string PercentWithTaxedDepositMessage =
        "หักมัดจำที่ออกใบกำกับแล้วใช้ร่วมกับส่วนลดท้ายบิลแบบ % ไม่ได้ — เปลี่ยนส่วนลดท้ายบิลเป็นจำนวนบาท";

    /// <summary>แยกยอดที่เฉลี่ยลงบรรทัดแล้ว (Σ ส่วนหักท้ายบิลจริง) กลับเป็น (ส่วนลดการค้า, ฐานมัดจำ) — ทั้งสองลดฐานภาษีเหมือนกัน
    /// จึงเฉลี่ยรวมกันครั้งเดียว · <c>Ok = false</c> = ส่วนลด + มัดจำเกินยอดขาย (ตัวเฉลี่ยตัดยอดทิ้ง) ⇒ ผู้เรียกต้องล้มดัง
    /// ห้ามให้ส่วนที่ถูกตัดไปหายเงียบจากช่องใดช่องหนึ่ง</summary>
    public static (decimal TradeDiscount, decimal DepositBase, bool Ok) SplitBillDeduction(
        decimal allocatedTotal, decimal requestedTrade, decimal depositBase)
    {
        if (depositBase <= 0m) return (allocatedTotal, 0m, true);
        var want = Math.Round(Math.Max(0m, requestedTrade), 2, MidpointRounding.AwayFromZero) + depositBase;
        return allocatedTotal == want
            ? (allocatedTotal - depositBase, depositBase, true)
            : (Math.Max(0m, allocatedTotal - depositBase), Math.Min(depositBase, allocatedTotal), false);
    }

    /// <summary>P0-3: ใบมัดจำนี้ "ออกใบกำกับแล้ว" (VAT เข้า ภ.พ.30 เดือนที่รับเงิน) ⇒ ห้ามนำไปหัก <b>เต็มจำนวน</b>
    /// เข้าใบกำกับที่คิด VAT เต็ม (ปุ่มหักมัดจำ / หักแบบขับ JE) — เดิมเส้นนั้นกลับ Dr 21911 ของมัดจำแล้วให้ใบสุดท้าย
    /// รายงาน VAT เต็ม + รายงานภาษีขายข้ามแถวมัดจำ ⇒ ผู้ซื้อถือใบกำกับสองใบสำหรับ VAT ก้อนเดียว (618.22 แทน 487.38)
    /// และ VAT มัดจำย้ายไปเดือนของใบสุดท้าย (§78/1 ช้า) · เดิมด่านมีเฉพาะ "งวดยื่นแล้ว" และเฉพาะปุ่ม ไม่ครอบเส้นขับ JE</summary>
    /// <param name="depositVatAmount">VAT ของใบมัดจำ (หรือขา Cr 21911 จริงใน GL)</param>
    /// <param name="depositVatPending">VAT ยังพักอยู่ 21913 (ยังไม่เข้า ภ.พ.30)</param>
    public static bool GrossApplyBlocked(decimal depositVatAmount, bool depositVatPending)
        => depositVatAmount > 0.005m && !depositVatPending;

    /// <summary>ฐาน (ก่อน VAT) ของมัดจำที่ออกใบกำกับแล้วที่จะหักออกจากฐานภาษีของใบสุดท้าย เมื่อผู้ใช้/คู่ค้าระบุ
    /// "หักมัดจำ" เป็นยอดรวม VAT (<paramref name="appliedGross"/>) — ตัวแปลงตัวเดียวของทุกเส้น (ปุ่ม · ฟอร์มขายเงินสด ·
    /// เช็คเอาต์ที่พัก) ให้ได้รูป "หักมูลค่ามัดจำ (ก่อน VAT) ตามใบกำกับภาษี" ⇒ VAT สองใบรวม = VAT ของยอดเต็ม ผู้ซื้อไม่ได้
    /// ใบกำกับสองใบสำหรับภาษีก้อนเดียว (รอบ 193 ฝ่ายค้านรอบสอง N1)
    /// <para>ยอดเต็มคงเหลือ = ฐานคงเหลือ (ไม่ต้องปัด) · บางส่วน = round(gross × ฐาน/รวม) ไม่เกินฐานคงเหลือ</para></summary>
    public static decimal TaxedDepositBase(decimal appliedGross, decimal depositSubTotal, decimal depositTotal, decimal remainingBase)
    {
        if (appliedGross <= 0m || depositTotal <= 0m || remainingBase <= 0m) return 0m;
        var b = Math.Round(appliedGross * depositSubTotal / depositTotal, 2, MidpointRounding.AwayFromZero);
        return Math.Min(b, remainingBase);
    }

    /// <summary>กระจายฐานที่ต้องรับรู้เป็นรายได้ (= ส่วนหักท้ายบิลของใบสุดท้าย − ที่รับรู้เพื่อใบนี้ไปแล้ว) ลงใบมัดจำ
    /// ที่อ้างถึง ใบเก่าสุดก่อน ไม่เกินฐานคงเหลือของแต่ละใบ · <c>Shortfall</c> &gt; 0 = มัดจำไม่พอกับที่ใบหักไว้ (ต้องล้มดัง)</summary>
    public static (IReadOnlyList<(Guid Id, decimal Base)> Lines, decimal Shortfall) AllocateBaseDeduction(
        decimal baseToRealize, IReadOnlyList<(Guid Id, decimal RemainingBase)> depositsOldestFirst)
    {
        var lines = new List<(Guid, decimal)>();
        var left = Math.Max(0m, baseToRealize);
        foreach (var (id, rem) in depositsOldestFirst)
        {
            if (left <= 0.005m) break;
            var take = Math.Min(left, Math.Max(0m, rem));
            if (take <= 0.005m) continue;
            lines.Add((id, take));
            left -= take;
        }
        return (lines, left > 0.005m ? left : 0m);
    }

    /// <summary>ข้อความของด่านใบเก่าที่ยังตั้ง "หักมัดจำแบบขับ JE" กับมัดจำออกใบกำกับแล้ว (ตอนอนุมัติ) — <b>ทางเดียว</b>
    /// (รอบ 193 ฝ่ายค้านรอบสาม R3-5): เดิมต่อท้าย <see cref="GrossApplyBlockedMessage"/> ซึ่งสั่ง "รับรู้มัดจำที่หน้าเงินมัดจำ"
    /// ขณะที่ทางแก้ของใบนี้ (บันทึกใหม่จากฟอร์ม) รับรู้ให้อัตโนมัติตอนอนุมัติ ⇒ ทำตามทั้งสองประโยค = รับรู้ซ้ำ</summary>
    public static string DrivesGuardMessage(string depositNumber, string documentNumber) =>
        $"⛔ ใบ {documentNumber} ตั้ง “หักมัดจำแบบลงบัญชีในใบเดียว” กับใบมัดจำ {depositNumber} ที่ออกเป็นใบกำกับภาษีแล้ว (§78/1) — "
        + "อนุมัติแบบนี้ไม่ได้ เพราะผู้ซื้อจะได้ใบกำกับสองใบสำหรับภาษีก้อนเดียว · ทางไปต่อ: เปิดแก้ใบนี้แล้วกดบันทึกจากฟอร์ม "
        + "(ระบบแปลงเป็น “หักมูลค่ามัดจำ (ก่อน VAT) ตามใบกำกับภาษี” และรับรู้มัดจำเป็นรายได้ให้เองตอนอนุมัติ) แล้วอนุมัติอีกครั้ง — "
        + "ไม่ต้องไปรับรู้มัดจำที่หน้า “เงินมัดจำ” (จะรับรู้ซ้ำ) · ถ้าใบนี้ถือเลขจริงแล้ว (กู้คืนจากการยกเลิก) ระบบไม่ให้แก้ยอดย้อนหลัง "
        + "ให้ยกเลิกใบนี้ถาวรแล้วสร้างใบใหม่จากฟอร์มแทน";

    /// <summary>ข้อความของด่าน P0-3 — บอกเหตุผล + ทางไปต่อทั้งสองทาง (ห้ามตันเฉย ๆ)</summary>
    public static string GrossApplyBlockedMessage(string depositNumber) =>
        $"⛔ ใบมัดจำ {depositNumber} ออกเป็นใบกำกับภาษีแล้ว (ภาษีขายเข้า ภ.พ.30 เดือนที่รับเงิน §78/1) — "
        + "หักเข้าใบกำกับที่คิด VAT เต็มจำนวนไม่ได้ เพราะผู้ซื้อจะได้ใบกำกับสองใบสำหรับภาษีก้อนเดียว และภาษีมัดจำจะย้ายเดือน · "
        + "ทางที่ถูก เลือกอย่างใดอย่างหนึ่ง: "
        + "① ออกใบกำกับใบสุดท้าย “หักมูลค่ามัดจำ (ก่อน VAT) ออกจากฐานภาษี” (ส่วนหักท้ายบิล = ฐานของมัดจำ) "
        + "แล้วรับรู้มัดจำเป็นรายได้ที่หน้า “เงินมัดจำ” — โมดูลที่พักทำให้อัตโนมัติ · "
        + "② ออกใบลดหนี้ (§86/10) ยกเลิกใบกำกับมัดจำเดิมก่อน แล้วค่อยออกใบกำกับเต็มจำนวน";

    /// <summary>"หักมัดจำแบบขับ JE" (<c>DepositAppliedDrivesJournal</c>) ใช้ได้กับชนิดที่ AutoPost อ่านธงนี้จริงเท่านั้น
    /// (ใบเสร็จ/ใบสำคัญรับ · ใบกำกับขายเงินสดใบเดียว) — ใบเครดิตรับธงแล้วไม่มีผล = silent no-op (P0-1 รอบ 193)</summary>
    public static bool DrivesJournalSupported(DocumentType type, bool issuedAsCashReceipt)
        => type is DocumentType.Receipt or DocumentType.ReceiptVoucher
           || (type == DocumentType.TaxInvoice && issuedAsCashReceipt);

    public const string DrivesUnsupportedMessage =
        "“หักมัดจำแบบลงบัญชีในใบเดียว” ใช้ได้กับใบเสร็จรับเงิน/ใบสำคัญรับ หรือใบกำกับภาษีแบบขายเงินสดใบเดียวเท่านั้น — "
        + "ใบแจ้งหนี้/ใบกำกับแบบเครดิตให้บันทึกใบก่อน แล้วกด “หักมัดจำ” หลังอนุมัติ (ระบบตัดลูกหนี้ด้วยมัดจำให้)";

    // ═══════════════════════════ รอบ 194 — ประเภทเงินมัดจำ (spec S1–S4 · S7) ═══════════════════════════

    public const string KindPriceServiceRuleCode = "RD-78/1";
    public const string KindPriceGoodsRuleCode = "RD-78(1)(b)";
    public const string KindSecurityEarlyVatRuleCode = "RD-PO73-SEC";
    public const string KindNonVatRuleCode = "RD-81";
    public const string SecurityDeductRuleCode = "DEP-SEC-DEDUCT";
    /// <summary>ธงบนใบเมื่อภาษีของเงินที่ริบถึงกำหนดย้อนหลัง (spec S3) — ข้อความเต็มจาก <see cref="LateVatNote"/></summary>
    public const string LateVatMarker = "[DEPOSIT-LATE-VAT]";
    /// <summary>ชื่อเมื่อไม่มีประเภท (ใบที่ตัดสินจากค่าเดิมของช่องทาง/บริษัท)</summary>
    public const string DefaultKindName = "มัดจำ/เงินรับล่วงหน้า";
    /// <summary>รอบ 194 ฝ่ายค้าน C1 — ประเภทที่ลักษณะไม่ตรงช่อง (เงินประกันที่ต้องคืนถูกตั้งเป็นมัดจำค่าห้อง/ประเภทเริ่มต้นบริษัท)
    /// ถูกข้ามไปชั้นถัดไป · ประเภทเริ่มต้น/ลักษณะของประเภทที่ถูกผูกเปลี่ยนให้ขัดช่องไม่ได้</summary>
    public const string KindNatureMismatchRuleCode = "DEP-KIND-NATURE";

    /// <summary>ลักษณะเงินนี้ใช้เป็น "มัดจำที่เป็นราคา" ได้ไหม — มัดจำค่าห้อง · ชำระล่วงหน้าหน้าจอง · <b>ประเภทเริ่มต้นบริษัท</b>
    /// (= มัดจำทั่วไปของทุกทางเข้าที่ไม่ระบุประเภท) ต้องเป็น <see cref="DepositNature.PartOfPrice"/> หรือ <see cref="DepositNature.NonVatSupply"/> ·
    /// เงินประกันที่ต้องคืนใช้ได้เฉพาะเมื่อผู้ใช้เลือกบนใบเอง/ช่องเงินประกันของที่พัก (รอบ 194 ฝ่ายค้าน C1: เดิมไม่มีใครตรวจ ⇒
    /// ใบมัดจำค่าห้องตรึงลักษณะเงินประกัน = ไม่เกิดภาษีตอนรับเงิน ขัด §78/1 · เช็คเอาต์ไม่หักมัดจำ · CMS ไม่รับรู้รายได้)</summary>
    public static bool AcceptableAsPriceDeposit(DepositNature? nature)
        => nature is DepositNature.PartOfPrice or DepositNature.NonVatSupply;

    /// <summary>ข้อความเมื่อประเภทที่ตั้งไว้ในช่องมัดจำราคาถูกข้ามเพราะลักษณะไม่ตรง (ผู้ใช้ต้องเห็น — ห้ามข้ามเงียบ)</summary>
    private static string KindNatureSkippedNote(string kindName, string where)
        => $"⚠️ [{KindNatureMismatchRuleCode}] ประเภท “{kindName}” ที่ตั้งเป็น{where}เป็นเงินประกันที่ต้องคืน — ใช้เป็นมัดจำที่เป็นส่วนหนึ่งของราคาไม่ได้ "
           + "(ภาษีขายถึงกำหนดตอนรับเงิน มาตรา 78/1) ระบบจึงข้ามไปใช้ชั้นถัดไป · แก้: เลือกประเภทลักษณะ “ส่วนหนึ่งของราคา” ในช่องนั้น";

    /// <summary>บัญชีเงินประกันรับ (spec S7 — ไม่เพิ่มเลขใหม่): ผังที่มี 21530 เงินประกันความเสียหาย (ผังโรงแรม) ใช้ 21530 ·
    /// ไม่งั้น 21620 เงินค้ำประกัน (ผังมาตรฐาน)</summary>
    private static string SecurityLiabilityAccountCode(bool chartHas21530) => chartHas21530 ? "21530" : "21620";

    /// <summary>ค่าที่รับจาก client/DB เป็นลักษณะที่นิยามไว้จริงไหม</summary>
    public static bool IsDefined(DepositNature? n) => n is DepositNature v && Enum.IsDefined(v);

    /// <summary>ประเภทนี้ใช้ในการตัดสินได้ไหม (มี · ไม่ลบ · เปิดใช้ · ลักษณะนิยามไว้ · เป็นของบริษัทนี้) — ไม่ได้ = ตกชั้นถัดไป</summary>
    private static bool Usable(DepositKind? k, Guid companyId)
        => k is not null && !k.IsDeleted && k.IsActive && k.CompanyId == companyId && IsDefined(k.Nature);

    /// <summary>
    /// <b>ตัวตัดสินประเภทเงินมัดจำตัวเดียว</b> (รอบ 194 · spec S4) — ลำดับ 6 ชั้น:
    /// ① ประเภทที่เลือกบนใบ → ② ประเภทของช่องทาง (ที่พัก/หน้าจอง) → ③ ค่าเดิมของช่องทาง (<c>LodgingProperty.DepositVatTreatment</c>) →
    /// ④ ประเภทเริ่มต้นบริษัท → ⑤ <c>CompanySettings.DepositVatTreatment</c> → ⑥ ประเภทธุรกิจ (VAT ทันที = พฤติกรรมเดิม)
    ///
    /// <para>ประเภทที่ไม่ตั้งโหมด (<c>VatTreatment</c> null · เช่น ADVANCE ที่ seed) ใช้ลักษณะ/ชื่อ/บัญชีของประเภท แต่โหมดตกไปชั้น
    /// ③→⑤→⑥ ⇒ ปุ่มตั้งค่าเดิมยังมีผล · ไม่มีประเภทเลย = ลักษณะ <see cref="DepositNature.PartOfPrice"/> (พฤติกรรมเดิม) ·
    /// ประเภทที่ปิด/ลบ/ของบริษัทอื่น = เหมือนไม่มี (ตกชั้นถัดไป — ผู้เรียกที่รับ id จาก client ต้องตรวจเองก่อนว่ามีจริง แล้วตอบ 400)</para>
    ///
    /// <para>⚠️ ผลนี้ใช้กับใบใหม่เท่านั้น · payload ที่ไม่ระบุประเภท (คู่ค้า/OCR/ฟอร์มเก่า) ⇒ ผู้เรียกส่ง decision = null เข้า
    /// <see cref="DepositDocumentShaping.Apply"/> ⇒ รูปใบเหมือนเดิมทุกตัวอักษร</para>
    /// </summary>
    /// <param name="companyId">บริษัทของใบ — ประเภทของบริษัทอื่นถูกทิ้ง (tenant isolation)</param>
    /// <param name="supply">ลักษณะสิ่งที่ขาย (<see cref="NatureOf"/> ของบริษัท · ที่พักส่ง Service)</param>
    /// <param name="documentKind">① ประเภทที่เลือกบนใบ</param>
    /// <param name="channelKind">② ประเภทของช่องทาง (เช่น <c>LodgingProperty.RoomDepositKindId</c>)</param>
    /// <param name="channelTreatment">③ ค่าเดิมของช่องทาง (<c>LodgingProperty.DepositVatTreatment</c>)</param>
    /// <param name="companyDefaultKind">④ ประเภทที่ <c>IsDefault</c> ของบริษัท</param>
    /// <param name="companySetting">⑤ <c>CompanySettings.DepositVatTreatment</c></param>
    /// <param name="chartHas21530">ผังของบริษัทมี 21530 ไหม (เลือกบัญชีเงินประกัน)</param>
    /// <param name="priceChannel">ช่องทางนี้รับ "มัดจำที่เป็นราคา" (มัดจำค่าห้อง · ชำระล่วงหน้าหน้าจอง) ⇒ ประเภทของช่องทาง (②) ที่ลักษณะไม่ผ่าน
    /// <see cref="AcceptableAsPriceDeposit"/> ถูกข้าม (ตกชั้นถัดไป + คำเตือน) · ชั้น ④ ประเภทเริ่มต้นบริษัทถูกกรองแบบนี้<b>เสมอ</b>
    /// (ค่าเริ่มต้น = มัดจำทั่วไปของทุกทางเข้า — เงินประกันเป็นค่าเริ่มต้นไม่ได้) · ชั้น ① ประเภทที่ผู้ใช้เลือกบนใบเองไม่ถูกกรอง</param>
    public static DepositKindDecision ResolveKind(
        Guid companyId,
        DepositSupplyNature supply,
        DepositKind? documentKind,
        DepositKind? channelKind,
        DepositVatTreatment? channelTreatment,
        DepositKind? companyDefaultKind,
        DepositVatTreatment? companySetting,
        bool chartHas21530 = false,
        bool priceChannel = false)
    {
        DepositKind? kind = null;
        var kindSource = DepositVatTreatmentSource.BusinessTypeDefault;
        string? skippedNote = null;
        var channelUsable = Usable(channelKind, companyId);
        if (channelUsable && priceChannel && !AcceptableAsPriceDeposit(channelKind!.Nature))
        {
            skippedNote = KindNatureSkippedNote(channelKind!.Name, "มัดจำของช่องทางนี้");
            channelUsable = false;
        }
        var defaultUsable = Usable(companyDefaultKind, companyId);
        if (defaultUsable && !AcceptableAsPriceDeposit(companyDefaultKind!.Nature))
        {
            // ข้อมูลที่ตั้งไว้ก่อนมีด่าน SetDefault — migration ถอดธงแล้ว แต่ตัวตัดสินต้องไม่เชื่อธงเอง (ทิศปลอดภัย = มองเห็น)
            if (!Usable(documentKind, companyId) && !channelUsable && !IsDefined(channelTreatment))
                skippedNote ??= KindNatureSkippedNote(companyDefaultKind!.Name, "ประเภทเริ่มต้นของบริษัท");
            defaultUsable = false;
        }
        if (Usable(documentKind, companyId)) { kind = documentKind; kindSource = DepositVatTreatmentSource.DocumentKind; skippedNote = null; }
        else if (channelUsable) { kind = channelKind; kindSource = DepositVatTreatmentSource.ChannelKind; }
        else if (!IsDefined(channelTreatment) && defaultUsable)
        { kind = companyDefaultKind; kindSource = DepositVatTreatmentSource.CompanyDefaultKind; }

        DepositVatTreatment t;
        DepositVatTreatmentSource source;
        if (kind is not null && IsDefined(kind.VatTreatment)) { t = kind.VatTreatment!.Value; source = kindSource; }
        else if (IsDefined(channelTreatment)) { t = channelTreatment!.Value; source = DepositVatTreatmentSource.ChannelOverride; }
        else if (IsDefined(companySetting)) { t = companySetting!.Value; source = DepositVatTreatmentSource.CompanySetting; }
        else { t = DepositVatTreatment.VatImmediate; source = DepositVatTreatmentSource.BusinessTypeDefault; }

        var nature = kind?.Nature ?? DepositNature.PartOfPrice;
        var (warning, code) = KindWarning(nature, t, supply);
        if (skippedNote != null)
        {
            // ข้ามแล้วต้องบอก (หน้าตั้งค่าที่พัก · หมายเหตุการจอง · audit อ่าน Warning ตัวเดียวกัน) — รหัสกฎของคำเตือนเดิมชนะถ้ามี
            warning = warning is null ? skippedNote : skippedNote + " · " + warning;
            code ??= KindNatureMismatchRuleCode;
        }
        string? account = null;
        if (!string.IsNullOrWhiteSpace(kind?.LiabilityAccountCode)) account = kind.LiabilityAccountCode.Trim();
        else if (nature == DepositNature.RefundableSecurity) account = SecurityLiabilityAccountCode(chartHas21530);
        return new DepositKindDecision(
            KindId: kind?.Id,
            Code: kind?.Code,
            Name: string.IsNullOrWhiteSpace(kind?.Name) ? DefaultKindName : kind.Name.Trim(),
            Treatment: t,
            Nature: nature,
            LiabilityAccountCode: account,
            Source: source,
            Warning: warning,
            RuleCode: code,
            RequiresReason: ReasonRequired(nature, t),
            NeedsOwnerChoice: source == DepositVatTreatmentSource.BusinessTypeDefault && supply == DepositSupplyNature.Unknown,
            Supply: supply,
            ForfeitAccountCode: string.IsNullOrWhiteSpace(kind?.ForfeitAccountCode) ? null : kind.ForfeitAccountCode.Trim(),
            PolicyReason: string.IsNullOrWhiteSpace(kind?.PolicyReason) ? null : kind.PolicyReason.Trim());
    }

    /// <summary>ลักษณะ "ราคา" + โหมดที่เลื่อน VAT (เต็มยอด/รอเรียกเก็บ) ⇒ ต้องมีเหตุผล (spec S2 — ไม่ปิดตัวเลือกตามคำตัดสิน #34)</summary>
    private static bool ReasonRequired(DepositNature nature, DepositVatTreatment treatment)
        => nature == DepositNature.PartOfPrice && treatment != DepositVatTreatment.VatImmediate;

    /// <summary>ด่านตอนบันทึกประเภท (หน้าตั้งค่า/API) — null = ผ่าน · ข้อความ = เหตุ + ทางไปต่อ (ผู้เรียกโยน BusinessRuleException)
    /// <para><paramref name="treatment"/> = โหมดที่ตั้งบนประเภท (null = ตามค่าตั้งบริษัท ⇒ ไม่บังคับเหตุผลที่ประเภท —
    /// คำเตือนระดับบริษัทอยู่ที่ <see cref="Resolve"/>)</para></summary>
    public static string? KindProblem(DepositNature nature, DepositVatTreatment? treatment, string? policyReason)
    {
        if (!IsDefined(nature)) return "ลักษณะเงินไม่ถูกต้อง — เลือก ส่วนหนึ่งของราคา · เงินประกันที่ต้องคืน · หรือ นอกระบบ VAT";
        if (treatment is { } tv && !IsDefined(tv))
            return "วิธีบันทึกไม่ถูกต้อง — เลือก เต็มยอด · ภาษีรอเรียกเก็บ · หรือ VAT ทันที (หรือเว้นว่าง = ตามค่าตั้งบริษัท)";
        if (treatment is not { } t || !ReasonRequired(nature, t) || !string.IsNullOrWhiteSpace(policyReason)) return null;
        return $"⛔ มัดจำที่เป็นส่วนหนึ่งของราคาเลือก “{LabelOf(t)}” ได้เมื่อระบุเหตุผลเท่านั้น — ภาษีขายของเงินที่เป็นราคาถึงกำหนดตอนรับเงิน "
            + "(สินค้า มาตรา 78(1)(ข) · บริการ มาตรา 78/1 · ป.73/2541) · ทางไปต่อ: ① เปลี่ยนเป็น “ออกใบกำกับภาษี รับรู้ VAT ทันที” · "
            + "② ถ้าเงินก้อนนี้เป็นเงินประกันที่ต้องคืนจริง ให้เปลี่ยนลักษณะเป็น “เงินประกัน (ต้องคืน)” · ③ ยืนยันโหมดนี้พร้อมพิมพ์เหตุผล/เลขสัญญา "
            + "(ระบบเก็บเหตุผลไว้บนใบมัดจำเป็นบันทึกภายใน — เห็นในหน้าเอกสาร ไม่พิมพ์บนเอกสารที่ส่งลูกค้า — และแสดงในคำเตือนตอนอนุมัติ)";
    }

    /// <summary>คำเตือนตามตาราง spec S2 (ลักษณะ × โหมด) — หน้าตั้งค่า · หมายเหตุบนใบ · คำเตือนตอนอนุมัติ ใช้ตัวนี้ตัวเดียว
    /// <para>ราคา × เลื่อน VAT: <b>สินค้าเตือนแรงเท่าบริการ</b> (รหัส <see cref="KindPriceGoodsRuleCode"/> / <see cref="KindPriceServiceRuleCode"/> ·
    /// ไม่ทราบ = อ้างทั้งสองมาตรา ใช้รหัสบริการ) · เงินประกัน × แยก VAT: เตือน (ภาษีก่อนเวลา) · นอกระบบ VAT: แจ้งว่า VAT 0 เสมอ</para></summary>
    public static (string? Warning, string? RuleCode) KindWarning(
        DepositNature nature, DepositVatTreatment treatment, DepositSupplyNature supply = DepositSupplyNature.Unknown)
    {
        if (nature == DepositNature.PartOfPrice && treatment != DepositVatTreatment.VatImmediate)
        {
            var what = treatment == DepositVatTreatment.FullDeposit ? "“มัดจำเต็มยอด ไม่แยก VAT”" : "“ภาษีรอเรียกเก็บ”";
            string law;
            string code;
            if (supply == DepositSupplyNature.Goods) { law = "ป.รัษฎากร มาตรา 78(1)(ข) — สินค้า: ได้รับชำระราคาก่อนส่งมอบ"; code = KindPriceGoodsRuleCode; }
            else if (supply == DepositSupplyNature.Service) { law = "ป.รัษฎากร มาตรา 78/1(1) — บริการ: ได้รับชำระ"; code = KindPriceServiceRuleCode; }
            else { law = "ป.รัษฎากร มาตรา 78(1)(ข) สินค้า / มาตรา 78/1(1) บริการ"; code = KindPriceServiceRuleCode; }
            return ($"⚠️ มัดจำที่เป็นส่วนหนึ่งของราคาบันทึกแบบ {what} — ภาษีขายถึงกำหนดทันทีในเดือนที่รับเงิน ({law} · ป.73/2541) · "
                    + "เลื่อนไปเดือนที่ออกใบสุดท้าย = นำส่งภาษีช้า เสี่ยงเงินเพิ่ม 1.5%/เดือน (มาตรา 89/1) และเบี้ยปรับ · "
                    + "ถ้าเป็นเงินประกันที่ต้องคืนจริง ให้เปลี่ยนลักษณะเงินของประเภทเป็น “เงินประกัน (ต้องคืน)”", code);
        }
        if (nature == DepositNature.RefundableSecurity && treatment != DepositVatTreatment.FullDeposit)
            return ("⚠️ เงินประกันที่ต้องคืนยังไม่ใช่ค่าตอบแทน (ป.73/2541) — บันทึกแบบแยก VAT = รับรู้ภาษีก่อนเกิดจุดความรับผิด "
                    + (treatment == DepositVatTreatment.VatImmediate
                        ? "(ออกใบกำกับภาษีได้ แต่ต้องออกใบลดหนี้ §86/10 ตอนคืน) "
                        : "(ภาษีรอเรียกเก็บไม่ต่างจากมัดจำเต็มยอดทางกฎหมาย) ")
                    + "· ค่าแนะนำ: “รับเป็นเงินมัดจำเต็มยอด ไม่แยก VAT”", KindSecurityEarlyVatRuleCode);
        if (nature == DepositNature.NonVatSupply)
            return ("ℹ️ สิ่งที่ขายอยู่นอกระบบภาษีมูลค่าเพิ่ม (ค่าเช่าอสังหาฯ §81(1)(ต) · ขายอสังหาฯ ที่เสียภาษีธุรกิจเฉพาะ §81(1)(น) · "
                    + "ยกเว้นอื่นตาม §81) — ระบบบันทึกใบมัดจำด้วย VAT 0 เสมอไม่ว่าเลือกวิธีบันทึกแบบใด", KindNonVatRuleCode);
        return (null, null);
    }

    /// <summary>ด่าน spec S2 แถวสุดท้าย: เงินประกันถูก "หักเป็นฐานภาษี/ราคา" (<c>DepositBaseDeducted</c> · หักแบบขับ JE) — null = ผ่าน
    /// (<paramref name="nature"/> null = ใบก่อนรอบ 194 ⇒ ไม่บล็อก — พฤติกรรมเดิม) · ข้อความ = เหตุ + ทางไปต่อ · รหัส <see cref="SecurityDeductRuleCode"/></summary>
    public static string? SecurityDeductionProblem(DepositNature? nature)
        => nature != DepositNature.RefundableSecurity ? null
         : $"⛔ [{SecurityDeductRuleCode}] เงินประกันที่ต้องคืนไม่ใช่ส่วนหนึ่งของราคา — นำไปหักออกจากฐานภาษี/ราคาของใบสุดท้ายไม่ได้ "
           + "(ฐานภาษีต้องเป็นมูลค่าเต็มของสินค้า/บริการ มาตรา 79) · ทางไปต่อ: ออกใบกำกับ/ใบแจ้งหนี้เต็มจำนวนก่อน แล้วกด "
           + "“ตัดชำระด้วยเงินประกัน” (หักมัดจำหลังอนุมัติ = รับชำระหนี้ ไม่ลดฐานภาษี) · หรือคืนเงินประกันที่หน้า “เงินมัดจำ”";

    /// <summary>
    /// VAT ของการริบมัดจำ — ตามลักษณะเงิน ไม่ใช่ตามโหมด (spec S3 · แก้ C-2/C-3 ของ L1) · <b>เรียกเฉพาะเมื่อ "ริบ"</b>
    /// (<c>RealizeDepositRequest.ForfeitAs != null</c> — รอบ 194 M5: การรับรู้ตามปกติไม่ผ่านตัวนี้)
    /// <para>ลำดับ: ① นอกระบบ VAT ⇒ ไม่มี VAT · ② VAT เสียไปแล้ว (ใบกำกับ/ย้ายเข้า 21911 แล้ว) ⇒ คงเดิม ไม่ออกใบลดหนี้ ·
    /// ③ ถือเป็นอะไร: ลักษณะ "ราคา" = ราคาเสมอ (ขอ "ค่าเสียหาย" = ไม่มีผล · <c>RequestIgnored</c>) · ลักษณะอื่น/ไม่ทราบ = ตามที่ขอ,
    /// ไม่ระบุ = <see cref="DepositForfeitAs.PriceOrFee"/> (ทิศปลอดภัย) · ④ ค่าเสียหาย ⇒ รายได้อื่นไม่มี VAT ·
    /// ⑤ ใบ VAT 0 <b>โดยชอบ</b> (ไม่ใช่การเลื่อน VAT — M2) ⇒ คงอัตราเดิม ไม่มี VAT ·
    /// ⑥ ราคา + VAT พัก 21913 ⇒ ย้ายเข้า 21911 · ⑦ ราคา + ยังไม่เคยแยก VAT (มัดจำเต็มยอด) ⇒ ออกใบกำกับยอดที่ริบ (บริษัทไม่จด VAT = ไม่มี VAT)</para>
    /// </summary>
    /// <param name="nature">ลักษณะเงินที่ตรึงบนใบมัดจำ (null = ใบเดิม ไม่ทราบ)</param>
    /// <param name="requested">ผู้ใช้ระบุว่าเงินที่ริบคืออะไร (null = ไม่ระบุ)</param>
    /// <param name="depositVatAmount">VAT บนใบมัดจำ (0 = มัดจำเต็มยอด/VAT 0 โดยชอบ/บริษัทไม่จด VAT)</param>
    /// <param name="vatPendingUnrecognized">VAT ยังพักอยู่ 21913 (<c>DepositOutputVatDeferred</c> และยังไม่รับรู้)</param>
    /// <param name="companyVatRate">อัตรา VAT ของบริษัท ณ วันริบ (0 = ไม่จด VAT)</param>
    /// <param name="depositOutputVatDeferred">ธง <c>DepositOutputVatDeferred</c> <b>ตามที่อยู่บนใบ</b> — ตัวนี้แปลงเป็นสามสถานะเองผ่าน
    /// <see cref="ForfeitZeroVatDeferred"/> (รอบ 194 R2-2: ธง false ของใบเดิมที่ไม่มีลักษณะเงิน = กำกวม ไม่ใช่ "VAT 0 โดยชอบ") ·
    /// <b>null = ผู้เรียกไม่ทราบ ⇒ ถือว่าเลื่อน</b> (ทิศปลอดภัย: ออกใบกำกับ = จ่ายเกินมองเห็นและแก้ได้)</param>
    /// <param name="channelVatRate">อัตรา VAT ของช่องทางที่ออกใบ (ที่พัก <c>ChargeVat=false</c> = 0) — เซิร์ฟเวอร์หาเองจากใบ ห้ามรับจาก client · null = ไม่มีช่องทาง</param>
    public static DepositForfeitVatDecision ForfeitVatDecision(
        DepositNature? nature, DepositForfeitAs? requested, decimal depositVatAmount, bool vatPendingUnrecognized,
        decimal companyVatRate = 7m, bool? depositOutputVatDeferred = null, decimal? channelVatRate = null)
    {
        DepositForfeitAs? req = requested is { } r && Enum.IsDefined(r) ? r : null;
        var hasVat = depositVatAmount > 0.005m;
        if (nature == DepositNature.NonVatSupply)
            return new DepositForfeitVatDecision(DepositForfeitVatAction.NonVatNoVat, req ?? DepositForfeitAs.PriceOrFee,
                false, false, false, 0m, "สิ่งที่ขายอยู่นอกระบบ VAT (§81) — เงินที่ริบเป็นรายได้ไม่มี VAT", KindNonVatRuleCode);
        if (hasVat && !vatPendingUnrecognized)
            return new DepositForfeitVatDecision(DepositForfeitVatAction.KeepExistingVat, req ?? DepositForfeitAs.PriceOrFee,
                false, false, false, 0m, "ภาษีขายของมัดจำนี้เสียไปแล้วในเดือนที่รับเงิน — ริบแล้ว VAT คงเดิม ไม่ออกใบลดหนี้", null);

        // R2-2 — "VAT 0 มาแต่แรก" รู้ได้จาก ① ใบรอบ 194+ (มีลักษณะเงิน · ธงเลื่อน false) ② ช่องทางอัตรา 0 ③ ผู้ใช้ยืนยันกับใบที่กำกวม
        // (ใบเดิม NULL · VAT 0 · ธง false = ก่อน 24/09 มัดจำเต็มยอดทุกใบก็หน้าตาแบบนี้) · ใบที่รู้ว่าเลื่อน ⇒ คำยืนยันไม่มีผล (บอกผู้ใช้)
        var zeroState = ForfeitZeroVatDeferred(nature, depositOutputVatDeferred, channelVatRate);
        var saysNoVat = req == DepositForfeitAs.OriginallyNoVat;
        var zeroAtIssue = !hasVat && (zeroState == false || (zeroState == null && saysNoVat));
        var noVatIgnored = saysNoVat && !zeroAtIssue;
        DepositForfeitAs? priceReq = saysNoVat ? DepositForfeitAs.PriceOrFee : req;

        var ignored = nature == DepositNature.PartOfPrice && priceReq == DepositForfeitAs.Compensation;
        var effective = nature == DepositNature.PartOfPrice ? DepositForfeitAs.PriceOrFee : priceReq ?? DepositForfeitAs.PriceOrFee;
        if (effective == DepositForfeitAs.Compensation)
            return new DepositForfeitVatDecision(DepositForfeitVatAction.CompensationNoVat, effective,
                false, hasVat, false, 0m,
                "ริบเป็นค่าเสียหาย (ไม่ใช่ค่าตอบแทนของการขาย) — รายได้อื่นไม่มี VAT · ถ้าที่จริงเป็นค่าของที่ใช้ไป/ค่าบริการ/"
                + "ค่าธรรมเนียมยกเลิก ให้เลือกแบบมี VAT", null);

        var note = ignored ? " · ประเภทนี้เป็นส่วนหนึ่งของราคา — ตัวเลือก “ค่าเสียหาย” ไม่มีผล"
            : noVatIgnored ? " · ใบนี้" + (hasVat ? "มี VAT พักไว้" : "บันทึกว่าเลื่อน VAT (มัดจำเต็มยอด)")
                + " — ตัวเลือก “ไม่มี VAT มาแต่แรก” ไม่มีผล"
            : "";
        ignored |= noVatIgnored;
        string? code = note.Length == 0 ? null : KindPriceServiceRuleCode;
        // "ภาษีย้อนหลัง" จริงเฉพาะเงินที่เป็นราคาตั้งแต่วันรับ (หรือใบเดิมที่ไม่รู้ลักษณะ — ทิศปลอดภัย: ให้คนตรวจเห็น) ·
        // เงินประกันที่ริบเป็นค่าของ/ค่าธรรมเนียม จุดความรับผิดคือ "วันที่หัก" (legal-L1 ข้อ 2b/4) ⇒ ไม่ใช่ภาษีค้างของเดือนที่รับเงิน
        var lateVat = nature != DepositNature.RefundableSecurity;
        if (hasVat)   // ถึงตรงนี้ = VAT ยังพัก 21913
            return new DepositForfeitVatDecision(DepositForfeitVatAction.ReclassifyUndueToDue, effective,
                lateVat, false, ignored, 0m,
                "ภาษีขายที่พักไว้ (21913) ย้ายเข้าภาษีขาย (21911)"
                + (lateVat ? " — ภาษีถึงกำหนดตั้งแต่เดือนที่รับเงิน" : " — ภาษีถึงกำหนดในเดือนที่หัก/ริบ") + note, code);
        if (companyVatRate <= 0m)
            return new DepositForfeitVatDecision(DepositForfeitVatAction.CompanyNotVatRegistered, effective,
                false, false, ignored, 0m, "บริษัทไม่ได้จดทะเบียน VAT — เงินที่ริบเป็นรายได้ไม่มี VAT" + note, code);
        // M2 (ทิศตรงข้าม) — VAT 0 ที่ไม่ได้เลื่อน = อัตราของสิ่งที่ขายเป็น 0/ยกเว้นจริง (หรือออกตอนยังไม่จด VAT · ช่องทางไม่คิด VAT)
        // ⇒ ริบแล้วยังเป็นราคาที่ VAT 0 — ห้ามออกใบกำกับอัตราบริษัท
        // (เดิม: มัดจำบริการส่งออก 0% 100,000 ริบแล้วได้ใบกำกับ VAT 6,542.06 + ธงสั่งยื่นเพิ่มเติม)
        if (zeroAtIssue)
            return new DepositForfeitVatDecision(DepositForfeitVatAction.ZeroVatAtIssue,
                zeroState == null ? DepositForfeitAs.OriginallyNoVat : effective,
                false, false, ignored, 0m,
                (zeroState == null
                    ? "ผู้ใช้ยืนยันว่าใบมัดจำนี้ไม่มี VAT มาแต่แรก (ส่งออก 0% §80/1 · ยกเว้น §81 · ออกตอนยังไม่จด VAT) — ริบแล้วไม่ออกใบกำกับภาษี "
                      + "· ถ้าที่จริงเป็นมัดจำเต็มยอดที่ยังไม่เคยเสีย VAT ให้เลือกแบบ “มี VAT” (ภาษีขายจะหาย)"
                    : "ใบมัดจำนี้ออกด้วย VAT 0 โดยชอบ (ยกเว้น §81 · อัตรา 0% §80/1 · ออกตอนยังไม่จด VAT · หรือช่องทางไม่คิด VAT) "
                      + "ไม่ใช่การเลื่อน VAT — ริบแล้วคงอัตราเดิม ไม่ออกใบกำกับภาษี") + note, code);
        return new DepositForfeitVatDecision(DepositForfeitVatAction.IssueTaxInvoiceForForfeit, effective,
            lateVat, false, ignored, companyVatRate,
            "มัดจำเต็มยอดที่เป็นค่าตอบแทนยังไม่เคยเสีย VAT — ต้องออกใบกำกับภาษีของยอดที่ริบ (ราคารวม VAT) แล้วตัดชำระด้วยมัดจำ "
            + "ห้ามลงรายได้ไม่มี VAT" + note, code);
    }

    /// <summary>
    /// รอบ 194 M4 + R2-1 — tax point + ธงของ VAT ที่เกิดจากการริบ (ใบกำกับของยอดที่ริบ · ย้าย 21913 → 21911) <b>ตัวตัดสินตัวเดียว</b>
    /// <para>ระบบ<b>ไม่รู้</b>ว่าผู้ใช้ยื่น ภ.พ.30 นอกระบบหรือไม่ ⇒ "ไม่มีแถวยื่นในระบบ" ≠ "ยังไม่ยื่น" (DOCTRINE §1: เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล
    /// ห้ามตกเป็น "ผ่าน") · R2-1: เดิมใช้แค่แถวยื่นในระบบ ⇒ บริษัทที่ยื่นทางเว็บ RD โดยไม่บันทึกในระบบ ได้ tax point = เดือนรับเงินที่ยื่นไปแล้ว
    /// ⇒ VAT ของการริบไม่อยู่ในแบบใดเลย และหมายเหตุบอก "ไม่ต้องยื่นเพิ่มเติม"</para>
    /// <list type="bullet">
    /// <item>ไม่ใช่ภาษีย้อนหลัง (<paramref name="lateVat"/> false — เงินประกันที่หักเป็นค่าของ/ค่าธรรมเนียม) ⇒ tax point = วันที่ริบ · ไม่มีธง</item>
    /// <item>ใช้<b>วันรับเงิน</b>ได้เฉพาะเมื่อ (เดือนรับเงิน = เดือนที่ริบ <b>หรือ</b> วันนี้ยังไม่เลยกำหนดยื่นของงวดเดือนรับเงิน — ตาราง
    /// <see cref="TaxFilingDeadline"/> ตัวเดียว · ใช้กำหนด<b>กระดาษ</b> (เร็วกว่า = ทิศปลอดภัย)) <b>และ</b> ไม่มีแถวยื่น/ล็อก/ปิดงวดในระบบ
    /// <b>และ</b> ไม่ข้ามปีภาษี</item>
    /// <item>นอกนั้น ⇒ VAT เข้างวดปัจจุบัน (วันที่ริบ) + ธง <see cref="LateVatMarker"/> ข้อความตรงความจริง: ถึงกำหนดงวดไหน · นำส่งงวดไหน ·
    /// อาจมีเงินเพิ่ม §89/1 · ห้ามนำส่งซ้ำ</item>
    /// </list>
    /// </summary>
    /// <param name="lateVat"><see cref="DepositForfeitVatDecision.LateVat"/> — ภาษีถึงกำหนดตั้งแต่วันรับเงินจริงไหม</param>
    /// <param name="depositReceivedDate">วันรับเงินมัดจำ (วันที่ของใบมัดจำ)</param>
    /// <param name="forfeitDate">วันที่ริบ/รับรู้ (วันที่ของใบกำกับ/JE ของการริบ)</param>
    /// <param name="depositPeriodLocked">งวดเดือนรับเงิน "ปิดในระบบแล้ว" — แถว ภ.พ.30 ยื่น/ประกาศว่ายื่น (<c>TaxFilingLockPolicy.DeclaredOrFiledStatuses</c>) ·
    /// <c>FilingLockedAt</c> · หรืองวดบัญชีของวันรับเงินปิดแล้ว</param>
    /// <param name="today">วันนี้ตามปฏิทินไทย (<c>ThaiDate.CalendarDateUtc(DateTime.UtcNow)</c>) — ใช้เทียบกำหนดยื่น (การยื่นเกิดตามเวลาจริง ไม่ใช่วันที่ในใบ)</param>
    public static DepositForfeitTaxPoint ForfeitTaxPointDecision(
        bool lateVat, DateTime depositReceivedDate, DateTime forfeitDate, bool depositPeriodLocked, DateTime today)
    {
        if (!lateVat) return new DepositForfeitTaxPoint(forfeitDate, false, null);
        if (forfeitDate < depositReceivedDate) return new DepositForfeitTaxPoint(forfeitDate, false, null);
        var sameMonth = SameVatPeriod(depositReceivedDate, forfeitDate);
        var crossYear = depositReceivedDate.Year != forfeitDate.Year;
        var deadline = TaxFilingDeadline.For(TaxFilingDeadline.KeyOf(TaxType.VAT)!, depositReceivedDate.Year, depositReceivedDate.Month).Paper;
        var beforeDeadline = today.Date <= deadline.Date;
        var receivedMonth = depositReceivedDate.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        var receivedDay = depositReceivedDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        if (!depositPeriodLocked && !crossYear && (sameMonth || beforeDeadline))
            return new DepositForfeitTaxPoint(depositReceivedDate, false, sameMonth ? null
                : $"ภาษีขายของยอดที่ริบเข้างวด ภ.พ.30 เดือน {receivedMonth} (เดือนที่รับเงิน {receivedDay}) — วันนี้ยังไม่เลยกำหนดยื่นงวดนั้น "
                  + $"({deadline.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}) · ถ้ายื่นงวดนั้นไปแล้วนอกระบบ ให้ยกเลิกรายการนี้แล้วบันทึกการยื่นก่อนทำใหม่");
        var nowMonth = forfeitDate.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        var why = depositPeriodLocked ? $"งวด {receivedMonth} ยื่น/ปิดในระบบแล้ว"
            : crossYear ? "ข้ามปีภาษีแล้ว"
            : $"เลยกำหนดยื่นงวด {receivedMonth} ({deadline.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}) แล้ว — ระบบไม่รู้ว่ายื่นนอกระบบไปแล้วหรือไม่";
        return new DepositForfeitTaxPoint(forfeitDate, true,
            $"{LateVatMarker} ภาษีถึงกำหนดงวด {receivedMonth} (รับเงิน {receivedDay}) · นำส่งในงวด {nowMonth} ({why}) · "
            + $"อาจมีเงินเพิ่ม §89/1 ของงวด {receivedMonth} · ห้ามนำส่งซ้ำ — ปรึกษานักบัญชี");
    }

    /// <summary>สองวันที่อยู่งวดภาษีรายเดือนเดียวกันไหม (ปี + เดือน) — ใช้ทั้งตัวตัดสิน tax point และการประทับ <c>DepositOutputVatRecognizedAt</c>
    /// ให้ตรงเดือนของ JE ที่ Cr 21911 จริง (R2-1)</summary>
    public static bool SameVatPeriod(DateTime a, DateTime b) => a.Year == b.Year && a.Month == b.Month;

    // ═══════════════════════════ รอบ 194 ทีม M — หลังฝ่ายค้านเงิน/ภาษี ═══════════════════════════

    /// <summary>รหัสกฎของ "รับรู้ตามปกติ (ไม่ใช่ริบ) โดยไม่มีใบสุดท้าย กับมัดจำเต็มยอดที่ยังไม่เคยเสีย VAT"</summary>
    public const string PlainRealizeRuleCode = "DEPOSIT-PLAIN-REALIZE";

    /// <summary>รหัสกฎของ "รับรู้ตามปกติ (ส่งมอบแล้ว) กับเงินประกันที่ต้องคืน" (R2-3)</summary>
    public const string SecurityPlainRealizeRuleCode = "DEP-SEC-PLAIN-REALIZE";

    /// <summary>
    /// รอบ 194 M5 + R2-2/R2-3 — "รับรู้ตามปกติ" (ส่งมอบ/ให้บริการแล้ว · <c>ForfeitAs</c> ว่าง · ไม่มีใบสุดท้าย) ทำได้ไหม — null = ได้ (เส้นเดิม: บัญชีที่ผู้เรียกระบุ)
    /// <list type="bullet">
    /// <item><b>เงินประกันที่ต้องคืน</b> ⇒ ไม่ได้เสมอ (R2-3: "ส่งมอบแล้ว" = รายได้ขาย 41000 ไม่มี VAT ทั้งที่เงินประกันไม่ใช่ราคา) · ทางไปต่อ 3 ทาง
    /// (คืน · ตัดชำระใบแจ้งหนี้ค่าของที่เสีย/ใช้ไป · ริบเป็นค่าเสียหาย)</item>
    /// <item>มัดจำเต็มยอดที่เป็นราคา (หรือใบเดิมไม่ทราบลักษณะ) ที่ยังไม่เคยเสีย VAT ของบริษัทที่จด VAT ⇒ ไม่ได้ (ภาษีขายหายเงียบ · spec S3) ·
    /// R2-2: ใบเดิม VAT 0 ที่ธงเลื่อน false = <b>กำกวม</b> ⇒ ทิศปลอดภัย = ถือว่ายังไม่เสีย VAT</item>
    /// </list>
    /// <para>ไม่บล็อก: ใบที่เสีย VAT แล้ว/VAT พัก (เส้นเดิมย้าย 21913→21911) · นอกระบบ VAT · ใบ VAT 0 ที่รู้แน่ (ใบรอบ 194+ ไม่เลื่อน · ช่องทางอัตรา 0) ·
    /// บริษัทไม่จด VAT</para>
    /// </summary>
    /// <param name="depositOutputVatDeferred">ธงเลื่อนตามที่อยู่บนใบ (ตัวนี้แปลงสามสถานะเองผ่าน <see cref="ForfeitZeroVatDeferred"/>)</param>
    /// <param name="channelVatRate">อัตราช่องทางที่เซิร์ฟเวอร์หาจากใบ (null = ไม่มีช่องทาง)</param>
    public static string? PlainRealizeProblem(
        DepositNature? nature, decimal depositVatAmount, bool depositOutputVatDeferred, decimal companyVatRate,
        decimal? channelVatRate = null)
    {
        if (nature == DepositNature.RefundableSecurity)
            return $"⛔ [{SecurityPlainRealizeRuleCode}] เงินประกันที่ต้องคืนไม่ใช่ค่าสินค้า/บริการ — “ส่งมอบแล้ว (รับรู้ตามปกติ)” ใช้ไม่ได้ "
                   + "(จะกลายเป็นรายได้ขายที่ไม่มี VAT) · ทางไปต่อ: ① คืนเงินประกันที่หน้า “เงินมัดจำ” · "
                   + "② ออกใบแจ้งหนี้/ใบกำกับค่าของที่เสีย/ใช้ไป แล้วกด “ตัดชำระด้วยเงินประกัน” (ใบนั้นคิด VAT ตามปกติ) · "
                   + "③ เลือก “ริบมัดจำ” → “ค่าเสียหายแท้” (รายได้อื่นไม่มี VAT)";
        if (depositVatAmount > 0.005m || companyVatRate <= 0m || nature == DepositNature.NonVatSupply) return null;
        if (ForfeitZeroVatDeferred(nature, depositOutputVatDeferred, channelVatRate) == false) return null;
        return $"⛔ [{PlainRealizeRuleCode}] มัดจำนี้รับเป็น “มัดจำเต็มยอด ไม่แยก VAT” และยังไม่เคยเสียภาษีขาย — รับรู้เป็นรายได้ตรง ๆ โดยไม่มีใบกำกับไม่ได้ "
               + "(ภาษีขายจะหาย) · ทางไปต่อ: ส่งมอบ/ให้บริการแล้ว ⇒ ออกใบกำกับภาษี/ใบแจ้งหนี้ แล้วกด “หักมัดจำ” · "
               + "ลูกค้ายกเลิก/ผิดสัญญา ⇒ เลือก “ริบมัดจำ” ในหน้าต่างรับรู้ (ระบบออกใบกำกับของยอดที่ริบแล้วตัดชำระด้วยมัดจำให้)"
               + (nature == null && !depositOutputVatDeferred
                   ? " · ใบเดิมนี้ถ้าไม่มี VAT มาแต่แรก (ส่งออก 0% / ยกเว้น §81 / ออกตอนยังไม่จด VAT) ให้ออกใบกำกับ/ใบแจ้งหนี้อัตราเดิม (0%/ยกเว้น) แล้ว “หักมัดจำ”"
                   : "");
    }

    /// <summary>หน้าศูนย์มัดจำเสนอตัวเลือก "ส่งมอบแล้ว (รับรู้ตามปกติ)" ไหม (R2-3) — เงินประกันที่ต้องคืน = ไม่เสนอ (หน้าเว็บซ่อน · ข้อมูลจากเซิร์ฟเวอร์)</summary>
    public static bool PlainRealizeOffered(DepositNature? nature) => nature != DepositNature.RefundableSecurity;

    /// <summary>รอบ 194 M3 — VAT พัก (21913) ที่ต้องย้ายเข้า 21911 ตอนรับรู้ = <b>ยอดที่ยังพักอยู่จริงใน GL</b> ไม่ใช่ VAT เต็มใบ
    /// (ริบเป็นค่าเสียหายบางส่วนกลับ 21913 เข้ารายได้ไปแล้ว · คืนบางส่วนตัด 21913 ไปแล้ว) · ไม่เกิน VAT ของใบ ไม่ติดลบ</summary>
    /// <param name="depositVatAmount">VAT บนใบมัดจำ</param>
    /// <param name="pendingUndueInGl">ยอด Cr สุทธิ 21913 ที่ใบนี้ยังค้าง — null = GL ไม่มีร่องรอย 21913 ของใบนี้เลย (ใบเก่า/ผังไม่มี) ⇒ ใช้ VAT เต็มใบตามเดิม</param>
    public static decimal UndueVatToRecognize(decimal depositVatAmount, decimal? pendingUndueInGl)
        => pendingUndueInGl is not decimal pending ? Math.Max(0m, depositVatAmount)
         : Math.Min(Math.Max(0m, depositVatAmount), Math.Max(0m, pending));

    /// <summary>รอบ 194 M3 — ภาษีขายที่รายงาน ภ.พ.30 ของมัดจำ VAT พักที่รับรู้แล้ว = ยอดที่ย้ายเข้า 21911 จริง (Cr 21911 ของ JE ที่ใบนี้เป็นต้นทาง)
    /// ไม่ใช่ VAT เต็มใบ · null/0 = ไม่มีร่องรอยใน GL ⇒ VAT เต็มใบ (พฤติกรรมเดิม)</summary>
    public static decimal ReportedRecognizedDepositVat(decimal depositVatAmount, decimal? reclassifiedInGl)
        => reclassifiedInGl is decimal moved && moved > 0.005m ? Math.Min(depositVatAmount, moved) : depositVatAmount;

    /// <summary>ฐานที่รายงานคู่กับ VAT ที่รายงาน (สัดส่วนเดียวกับ VAT ของใบ · ปัด AwayFromZero) — VAT ใบเป็น 0 หรือรายงานเต็ม ⇒ ฐานเดิม</summary>
    public static decimal ReportedRecognizedDepositBase(decimal vatableBase, decimal depositVatAmount, decimal reportedVat)
        => depositVatAmount <= 0.005m || reportedVat >= depositVatAmount - 0.005m ? vatableBase
         : Math.Round(vatableBase * reportedVat / depositVatAmount, 2, MidpointRounding.AwayFromZero);

    /// <summary>รอบ 194 P1 (regsec) — อัตราที่ใช้จัดรูปใบมัดจำ = อัตราบริษัท เว้นแต่ช่องทาง (ที่พัก <c>ChargeVat=false</c>) บอกอัตราที่ต่ำกว่า ·
    /// ช่องทางส่งสูงกว่าบริษัท = ไม่มีผล (ห้ามคิด VAT เกินที่บริษัทคิดได้ · §90/2) · ติดลบ = 0</summary>
    public static decimal ShapingVatRate(decimal companyVatRate, decimal? channelVatRate)
        => channelVatRate is decimal ch ? Math.Max(0m, Math.Min(companyVatRate, ch)) : companyVatRate;

    /// <summary>
    /// รอบ 194 P1/M2 + R2-2 — ใบมัดจำ VAT 0 นี้ "เลื่อน VAT (มัดจำเต็มยอด)" หรือ "ไม่มี VAT มาแต่แรก" — <b>สามสถานะ</b>
    /// <list type="bullet">
    /// <item><c>false</c> = รู้แน่ว่า VAT 0 มาแต่แรก: ช่องทางบอกอัตรา ≤ 0 (ที่พัก <c>ChargeVat=false</c> — ใบเก่าที่ถูกจัดรูปเป็นเต็มยอดด้วยอัตราบริษัทได้ผลถูกด้วย)
    /// หรือใบมีลักษณะเงิน (ใบรอบ 194+ — ธงเลื่อนถูกตั้งโดยตัวจัดรูปตัวเดียว เชื่อถือได้) และธง = false</item>
    /// <item><c>true</c> = เลื่อน VAT: ธงบนใบ = true · หรือผู้เรียกไม่ส่งธง (null — ทิศปลอดภัย)</item>
    /// <item><c>null</c> = <b>กำกวม</b>: ใบเดิม (ลักษณะ NULL) ธง = false — ก่อน 24/09 มัดจำเต็มยอดทุกใบก็เป็น VAT 0 + ธง false ⇒ ตัวตัดสินถือว่าเลื่อน
    /// (ทิศปลอดภัย spec S3) แต่หน้าศูนย์มัดจำเสนอตัวเลือก <see cref="DepositForfeitAs.OriginallyNoVat"/> ให้ผู้ใช้ยืนยัน</item>
    /// </list>
    /// </summary>
    public static bool? ForfeitZeroVatDeferred(DepositNature? nature, bool? documentDeferred, decimal? channelVatRate)
    {
        if (channelVatRate is decimal ch && ch <= 0m) return false;
        if (documentDeferred is not bool deferred) return true;
        if (deferred) return true;
        return nature != null ? (bool?)false : null;
    }

    /// <summary>รหัสบัญชีรายได้อื่น (ค่าปรับ/ค่าเสียหายที่ได้รับ → รายได้อื่นๆ) ของผังมาตรฐาน — ปลายทางของ "ริบเงินประกันเป็นค่าเสียหาย"
    /// เมื่อประเภทไม่ได้ตั้งบัญชีริบ (P-c: ห้ามตกไปรายได้ค่าห้อง/รายได้ขายซึ่งไม่มีใน ภ.พ.30 ⇒ กระทบยอด GL↔ภ.พ.30 ไม่ลง)</summary>
    public static readonly string[] CompensationIncomeAccountCodes = { "43080", "43070" };

    /// <summary>
    /// รอบ 194 M5/P-c — บัญชีรายได้ของการรับรู้/ริบมัดจำ (ตัวเดียว) · <paramref name="forfeitAction"/> null = รับรู้ตามปกติ
    /// <list type="bullet">
    /// <item>รับรู้ตามปกติ / ริบแบบราคา ⇒ ที่ผู้เรียกระบุ → 41000 → 42000 → บัญชีรายได้ใดก็ได้ (เส้นเดิม — บัญชีริบของประเภท<b>ไม่</b>ทับ)</item>
    /// <item>ริบ นอกระบบ VAT ⇒ ที่ผู้เรียกระบุ → บัญชีริบของประเภท → 41000 → 42000 → ใดก็ได้</item>
    /// <item>ริบเป็นค่าเสียหาย ⇒ ที่ผู้เรียกระบุ → บัญชีริบของประเภท → 43080 → 43070 → <b>ล้มดัง</b> (ห้ามตกไปรายได้ขาย)</item>
    /// </list>
    /// ค่าผู้ใช้ชนะเสมอ (กฎ #4 A) — เดิมบัญชีริบของประเภททับบัญชีที่ผู้ใช้ระบุเงียบ ๆ
    /// </summary>
    public static DepositRevenueAccountPlan RevenueAccountPlan(
        DepositForfeitVatAction? forfeitAction, string? requestCode, string? kindForfeitCode)
    {
        var codes = new List<string>();
        void Add(string? c) { if (!string.IsNullOrWhiteSpace(c) && !codes.Contains(c.Trim())) codes.Add(c.Trim()); }
        Add(requestCode);
        var compensation = forfeitAction == DepositForfeitVatAction.CompensationNoVat;
        if (compensation || forfeitAction == DepositForfeitVatAction.NonVatNoVat) Add(kindForfeitCode);
        if (compensation)
            foreach (var c in CompensationIncomeAccountCodes) Add(c);
        else { Add("41000"); Add("42000"); }
        return new DepositRevenueAccountPlan(codes, compensation, forfeitAction != null);
    }

    /// <summary>ข้อความล้มดังเมื่อไม่พบบัญชีรายได้ของการริบเป็นค่าเสียหาย (P-c) — บอกทางไปต่อ</summary>
    public const string CompensationAccountMissingMessage =
        "ไม่พบบัญชีรายได้สำหรับ “ริบเป็นค่าเสียหาย” (43080 รายได้ค่าปรับ/ค่าเสียหายที่ได้รับ หรือ 43070 รายได้อื่นๆ) — "
        + "ทางไปต่อ: ตั้ง “บัญชีรายได้เมื่อริบ” ในประเภทเงินมัดจำ (ตั้งค่า → ประเภทเงินมัดจำ) หรือเพิ่มบัญชีรายได้อื่นในผังบัญชี "
        + "แล้วทำรายการนี้ใหม่ (ห้ามลงรายได้ขาย — ค่าเสียหายไม่มีใน ภ.พ.30)";

    /// <summary>
    /// รอบ 194 P-e — ใบมัดจำที่ "ถูกหักจริง" จากเลขอ้างอิง (ลำดับเดียวกับเส้นหักจริง: เลขเอกสารก่อน แล้วค่อยเลขอ้างอิงต้นทาง)
    /// <para>ผู้เรียกกรองมาแล้วเฉพาะใบที่ออกแล้ว ไม่ยกเลิก (<c>DocumentStatusRules</c>) — ใบร่าง/void ไม่ใช่สิ่งที่ถูกหัก ·
    /// เลขอ้างอิงร่วม (การจองที่พัก: มัดจำค่าห้องกับเงินประกันใช้เลขจองเดียวกัน) ที่มีใบที่ไม่ใช่เงินประกัน ⇒ ใบที่ถูกหักคือใบนั้น
    /// (เงินประกันหักเป็นราคาไม่ได้อยู่แล้ว) · เลขที่ชี้เงินประกันอย่างเดียว ⇒ เงินประกันคือสิ่งที่ถูกหัก (ผู้เรียกบล็อก)</para>
    /// </summary>
    public static IReadOnlyList<DepositRefCandidate> ResolveDeductedDeposits(
        IReadOnlyList<string> refs, IReadOnlyList<DepositRefCandidate> effectiveCandidates)
    {
        var picked = new List<DepositRefCandidate>();
        var seen = new HashSet<Guid>();
        foreach (var rf in refs)
        {
            var byNumber = effectiveCandidates.Where(c => string.Equals(c.DocumentNumber, rf, StringComparison.Ordinal)).ToList();
            var hits = byNumber.Count > 0 ? byNumber
                : effectiveCandidates.Where(c => c.Reference != null && string.Equals(c.Reference, rf, StringComparison.Ordinal)).ToList();
            if (byNumber.Count == 0 && hits.Any(c => c.Nature != DepositNature.RefundableSecurity))
                hits = hits.Where(c => c.Nature != DepositNature.RefundableSecurity).ToList();
            foreach (var h in hits)
                if (seen.Add(h.Id)) picked.Add(h);
        }
        return picked;
    }

    /// <summary>รอบ 194 P-e — ปัญหา "หักเงินประกันเป็นฐาน/ราคา" ของเลขอ้างอิงชุดนี้ (ตัดสินจากใบที่ถูกหักจริง) — null = ผ่าน ·
    /// ข้อความ = เลขใบ + <see cref="SecurityDeductionProblem"/></summary>
    public static string? SecurityDeductionProblemForRefs(
        IReadOnlyList<string> refs, IReadOnlyList<DepositRefCandidate> effectiveCandidates)
    {
        foreach (var d in ResolveDeductedDeposits(refs, effectiveCandidates))
            if (SecurityDeductionProblem(d.Nature) is { } p) return $"ใบมัดจำ {d.DocumentNumber}: {p}";
        return null;
    }

    /// <summary>
    /// รอบ 194 M6 — ใบมัดจำที่ "VAT พัก 21913 ยังเปิดอยู่จริง" (ใช้กรองคำเตือน "ค้างเกิน 90 วัน" ตอนเปิดรายงาน ภ.พ.30) —
    /// ใบที่รับรู้/ริบครบ (<c>DepositRealizedAt</c>) หรือรับรู้+คืนรวมกันครบฐานแล้ว ไม่มีอะไรค้าง ⇒ ห้ามฟ้อง
    /// (เดิมริบเป็นค่าเสียหายครบ ไม่ประทับ <c>DepositOutputVatRecognizedAt</c> โดยตั้งใจ ⇒ ถูกฟ้องทุกงวดตลอดไป = ปิดด่านโดยไม่ตั้งใจ)
    /// <para>รูป expression ให้ EF แปลเป็น SQL ได้ · เทสต์ compile แล้วรันกับ entity ตรง ๆ (สูตรเดียวกัน)</para>
    /// </summary>
    public static Expression<Func<Document, bool>> UndueVatStillOpen =>
        d => d.DepositRealizedAt == null
             && d.SubTotal - d.DepositRealizedAmount
                - (d.TotalAmount > 0m ? d.DepositRefundedAmount * d.SubTotal / d.TotalAmount : 0m) > 0.005m;
}
