using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// "ใบไหนเป็นเจ้าของภาษีขาย" — <b>ตัวตัดสินเดียว</b> (รอบ 225 ฝ่ายค้าน B1) ของทั้ง loop หลักของรายงาน ภ.พ.30
/// (<c>TaxService.GenerateVatReport</c>) และการดึงเอกสารเข้ารายงานด้วยมือ (<see cref="TaxReportPullEligibility"/>).
///
/// <para>ที่มา: เดิมกติกานี้อยู่ inline ใน loop หลักเท่านั้น ⇒ ปุ่ม "ดึงเอกสาร" ดึงใบแจ้งหนี้ที่ออกใบกำกับอ้างแล้ว ·
/// ใบแจ้งหนี้บริการที่ VAT ยังพัก 21913 (ยังไม่รับเงิน §78/1) · ใบแจ้งหนี้ที่ใบเสร็จมี VAT เป็นเจ้าของ · ใบเสร็จที่แค่รับชำระ ·
/// มัดจำ VAT พัก/ถูกนำไปหักแล้ว ⇒ ภาษีขายนับซ้ำหรือก่อนถึงกำหนด</para>
///
/// <para>ตัวนี้ตัดสินเฉพาะ "ความเป็นเจ้าของ + วันที่ VAT ถึงกำหนด" — ไม่ตัดสินงวด (loop หลักเทียบงวดเองด้วยขอบเดิมทุกตัวอักษร ·
/// การดึงเทียบด้วย <see cref="TaxReportPullEligibility"/>)</para>
/// </summary>
public static class OutputVatOwnership
{
    public const string RuleSuperseded = "OUT-VAT-SUPERSEDED";
    public const string RuleOwnedByReceipt = "OUT-VAT-OWNED-BY-RECEIPT";
    public const string RuleNotDue21913 = "OUT-VAT-NOT-DUE-21913";
    public const string RuleSettlementOnly = "OUT-VAT-SETTLEMENT-ONLY";
    public const string RuleDepositApplied = "OUT-VAT-DEPOSIT-APPLIED";
    public const string RuleDepositNotRecognized = "OUT-VAT-DEPOSIT-NOT-RECOGNIZED";
    public const string RuleNotOutputDocument = "OUT-VAT-NOT-OUTPUT-DOC";

    /// <summary>สาขาของ loop หลัก — Invoice = "ใบกำกับ/ใบแจ้งหนี้" · Receipt = "ใบเสร็จ/ใบสำคัญรับที่ถือ VAT เอง" · None = ไม่ใช่ทั้งสอง</summary>
    public enum Branch { None, Invoice, Receipt }

    /// <summary>ข้อเท็จจริงของใบหนึ่ง (ส่วนที่ต้อง query ผู้เรียกเตรียมมา — ใช้ loader ชุดเดียวกันใน TaxService)</summary>
    public sealed record Facts(
        DocumentType Type,
        decimal VatAmount,
        DateTime TaxPoint,                 // TaxPointDate ?? DocumentDate
        DateTime? OutputVatDueAt,
        bool HasRelated,
        DocumentType? RelatedType,         // null = มี FK แต่หาใบไม่เจอ / ไม่มี FK
        DateTime? RelatedOutputVatDueAt,
        bool IsDeposit,
        bool DepositOutputVatDeferred,
        bool DepositApplied,
        DateTime? DepositOutputVatRecognizedAt,
        bool SupersededByTaxInvoice,       // มีใบกำกับ (ไม่ยกเลิก/ไม่ถูกตีกลับ) อ้างใบแจ้งหนี้นี้
        bool OwnedByVatReceipt,            // มีใบเสร็จที่มี VAT อ้างใบแจ้งหนี้นี้
        decimal Gl21913Net);               // ยอด Cr−Dr บน 21913 ของ JE ที่ใบนี้เป็นต้นทาง

    public static Facts FactsOf(Document d, bool supersededByTaxInvoice, bool ownedByVatReceipt, decimal gl21913Net,
        DocumentType? relatedType, DateTime? relatedOutputVatDueAt)
        => new(d.DocumentType, d.VatAmount, d.TaxPointDate ?? d.DocumentDate, d.OutputVatDueAt,
            d.RelatedDocumentId.HasValue, relatedType, relatedOutputVatDueAt,
            d.IsDeposit, d.DepositOutputVatDeferred, d.DepositAppliedToDocumentId.HasValue, d.DepositOutputVatRecognizedAt,
            supersededByTaxInvoice, ownedByVatReceipt, gl21913Net);

    /// <param name="Owns">ใบนี้เป็นเจ้าของภาษีขาย และ VAT ถึงกำหนดแล้ว</param>
    /// <param name="EffectivelyDeferred">มัดจำที่ VAT พัก (ธง หรือ GL 21913 ค้าง) — วันที่ถึงกำหนด = วันรับรู้</param>
    /// <param name="VatDueDate">วันที่ VAT ถึงกำหนด (ใช้หาเดือนภาษี) · null เมื่อไม่ Owns</param>
    public sealed record Decision(Branch Branch, bool Owns, bool EffectivelyDeferred, DateTime? VatDueDate,
        string? RuleCode, string? Reason);

    private static Decision Own(Branch b, DateTime due, bool deferred = false) => new(b, true, deferred, due, null, null);
    private static Decision Not(Branch b, string rule, string reason, bool deferred = false) => new(b, false, deferred, null, rule, reason);

    /// <summary>สาขาของ loop หลัก (เงื่อนไขเดิมทุกตัวอักษร — เทสต์ <c>OutputVatOwnershipTests</c> เทียบกับสำเนาโค้ดเดิม)</summary>
    private static Branch BranchOf(Facts f)
    {
        if (f.Type == DocumentType.TaxInvoice
            || (f.Type == DocumentType.Invoice && f.VatAmount > 0 && !f.SupersededByTaxInvoice))
            return Branch.Invoice;
        if ((f.Type == DocumentType.Receipt || f.Type == DocumentType.ReceiptVoucher)
            && (!f.HasRelated
                || f.RelatedType is DocumentType.Quotation or DocumentType.BillingNote
                || (f.VatAmount > 0.005m && f.RelatedType == DocumentType.Invoice && f.RelatedOutputVatDueAt != null)))
            return Branch.Receipt;
        return Branch.None;
    }

    public static Decision Decide(Facts f)
    {
        var branch = BranchOf(f);
        switch (branch)
        {
            case Branch.Invoice:
                if (f.Type != DocumentType.Invoice) return Own(branch, f.TaxPoint);
                if (f.OutputVatDueAt != null)
                    return f.OwnedByVatReceipt
                        ? Not(branch, RuleOwnedByReceipt,
                            "ใบเสร็จรับเงินที่มี VAT อ้างใบแจ้งหนี้นี้เป็นผู้ถือภาษีขายแล้ว — ดึงใบเสร็จนั้นแทน มิฉะนั้นภาษีขายนับซ้ำ")
                        : Own(branch, f.OutputVatDueAt.Value);
                if (f.Gl21913Net > 0.005m)
                    return Not(branch, RuleNotDue21913,
                        "ใบแจ้งหนี้บริการ VAT ยังพักที่ 21913 (ยังไม่รับเงิน · ยังไม่ถึงจุดความรับผิด §78/1) — "
                        + "ภาษีขายจะเข้ารายงานเดือนที่รับเงินเอง");
                return Own(branch, f.TaxPoint);

            case Branch.Receipt:
                var deferred = f.IsDeposit && (f.DepositOutputVatDeferred || f.Gl21913Net > 0.005m);
                if (deferred)
                {
                    if (f.DepositApplied)
                        return Not(branch, RuleDepositApplied,
                            "มัดจำนี้ถูกนำไปหักในใบกำกับ/ใบเสร็จปลายทางแล้ว — VAT รายงานที่ใบปลายทาง", true);
                    if (f.DepositOutputVatRecognizedAt == null)
                        return Not(branch, RuleDepositNotRecognized,
                            "มัดจำนี้ VAT ยังพักอยู่ (21913) ยังไม่รับรู้ภาษีขาย — เข้ารายงานเดือนที่รับรู้", true);
                    return Own(branch, f.DepositOutputVatRecognizedAt.Value, true);
                }
                if (f.IsDeposit && f.DepositApplied)
                    return Not(branch, RuleDepositApplied,
                        "มัดจำนี้ถูกนำไปหักในใบกำกับ/ใบเสร็จปลายทางแล้ว — VAT รายงานที่ใบปลายทาง");
                return Own(branch, f.TaxPoint);

            default:
                return NotOwner(f, branch);
        }
    }

    /// <summary>รอบ 269 (ผู้ใช้รายงาน ก.ย. 2569: ช่อง 7 "ส่งออก §80/1" 1,235,494.24 ทั้งที่บริษัทไม่มีขาย 0% เลย) —
    /// <b>ตัวตัดสินเดียว</b>ของ "บรรทัดของใบไหนนับเข้าช่องยอดขาย 5/7/8 ของ ภ.พ.30"
    ///
    /// <para>เดิม <c>GenerateVatReport</c> บวก <c>Pp30SalesClassifier.Split</c> ของ<b>ทุกใบฝั่งขาย</b>ที่ออกแล้ว ⇒ ใบเสร็จรับชำระ
    /// ("รับชำระเงินตามใบกำกับภาษีเลขที่ …" บรรทัด VatRate 0) · ใบวางบิล · ใบส่งของ · ใบเสนอราคาที่อนุมัติ · ใบแจ้งหนี้ที่ออกใบกำกับแทนแล้ว ·
    /// ใบแจ้งหนี้ที่ใบเสร็จถือ VAT · มัดจำที่ถูกหักแล้ว ถูกนับเป็นยอดขาย 0%/ยกเว้น ซ้ำกับใบที่ถือการขายจริง</para>
    ///
    /// <para>กติกา: นับเฉพาะใบที่ <see cref="Decide"/> บอกว่าเป็นเจ้าของภาษีขาย (ชุดเดียวกับแถว §87) — งวด = <see cref="Decision.VatDueDate"/>
    /// (วันเดียวกับวันที่ของแถว §87) · <b>ยกเว้นข้อเดียว</b> (คำตัดสิน 269-1-2): ใบแจ้งหนี้ที่<b>ไม่มี VAT ทั้งใบ</b> (0%/ยกเว้นล้วน) และไม่ถูก
    /// ใบกำกับแทน = ใบที่ถือการขายนั้นเอง (การขายยกเว้น §81 ออกใบกำกับไม่ได้ จึงอยู่บนใบแจ้งหนี้/ใบเสร็จเท่านั้น) — <see cref="BranchOf"/>
    /// ไม่ให้แถว §87 เพราะไม่มีภาษีให้รายงาน แต่ยอดต้องอยู่ช่อง 7/8 (ถ้าไม่นับ = ช่อง 8 หายทั้งช่อง · ผลตรวจ C-T01)</para>
    ///
    /// <para>ใบลดหนี้/เพิ่มหนี้ไม่ผ่านตัวนี้ — ผู้เรียกตัดสินฝั่งด้วย <c>ResolveCnDnSide</c> แยก</para></summary>
    public static Decision SalesBoxOwner(Facts f)
    {
        var d = Decide(f);
        if (d.Owns) return d;
        if (d.Branch == Branch.None && f.Type == DocumentType.Invoice && f.VatAmount <= 0m
            && !f.SupersededByTaxInvoice && !f.OwnedByVatReceipt)
            return Own(Branch.None, f.TaxPoint);
        return d;
    }

    private static Decision NotOwner(Facts f, Branch branch)
    {
        if (f.Type == DocumentType.Invoice && f.SupersededByTaxInvoice)
            return Not(branch, RuleSuperseded,
                "ออกใบกำกับภาษีอ้างใบแจ้งหนี้นี้แล้ว — ภาษีขายอยู่ที่ใบกำกับ ดึงใบกำกับแทน");
        if (f.Type is DocumentType.Receipt or DocumentType.ReceiptVoucher)
            return Not(branch, RuleSettlementOnly,
                "ใบเสร็จนี้รับชำระใบแจ้งหนี้/ใบกำกับ — ภาษีขายอยู่ที่ใบต้นทาง ไม่ใช่ใบเสร็จ");
        return Not(branch, RuleNotOutputDocument, "เอกสารนี้ไม่ได้เป็นผู้ถือภาษีขาย");
    }
}
