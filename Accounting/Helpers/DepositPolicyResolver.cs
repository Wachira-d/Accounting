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
            + "เต็มราคา · ริบมัดจำ = รายได้ไม่มี VAT · เหมาะกับเงินประกัน/มัดจำที่ต้องคืน (ยังไม่เกิดจุดความรับผิด)",
        DepositVatTreatment.VatPendingUndue =>
            "ใบมัดจำเป็น \"ใบเสร็จรับเงิน\" แยกฐาน + ภาษีขายรอเรียกเก็บ (21913 · ยังไม่เข้า ภ.พ.30) — ย้ายเป็นภาษีขาย (21911) "
            + "เมื่อออกใบสุดท้าย/ริบมัดจำ · ระบบเตือนใบที่ค้างเกิน 90 วันตอนเปิดรายงาน ภ.พ.30",
        _ =>
            "ใบมัดจำเป็น \"ใบกำกับภาษี/ใบเสร็จรับเงิน\" (เลขชุดใบกำกับ) ภาษีขาย (21911) เข้า ภ.พ.30 เดือนที่รับเงิน — "
            + "ใบสุดท้ายหักมูลค่ามัดจำ (ก่อน VAT) ออกจากฐานภาษี จึงไม่นับ VAT ซ้ำ และผู้ซื้อไม่เคลมภาษีซื้อซ้ำ",
    };

    private static string LegalReferenceOf(DepositVatTreatment t) => t switch
    {
        DepositVatTreatment.VatImmediate => "ป.รัษฎากร มาตรา 78/1 (บริการ: ได้รับชำระ) · มาตรา 78 (สินค้า: รับชำระก่อนส่งมอบ) · มาตรา 86/4",
        _ => "ป.รัษฎากร มาตรา 78 / 78/1 — ใช้ได้เมื่อยังไม่เกิดจุดความรับผิด (เงินประกัน/มัดจำที่ไม่ใช่ส่วนหนึ่งของราคา)",
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
        IndustryType.Service or IndustryType.Hotel or IndustryType.Construction or IndustryType.RealEstate
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
            : ($"ℹ️ {what} ถูกต้องเฉพาะเงินประกัน/มัดจำที่ยังไม่ใช่ส่วนหนึ่งของราคา — มัดจำค่าสินค้าที่รับก่อนส่งมอบ "
               + "ภาษีขายถึงกำหนดตอนได้รับชำระ (ป.รัษฎากร มาตรา 78)",
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
}
