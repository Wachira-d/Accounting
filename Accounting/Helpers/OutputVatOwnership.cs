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
    /// <summary>รอบ 269 H1 — ใบยอดยกมา (ลูกหนี้/เจ้าหนี้ยกมา OB-AR/OB-AP) ไม่ใช่การขาย/การซื้อของงวดใด ⇒ ไม่เข้าช่องใดของ ภ.พ.30 ทั้งสองฝั่ง</summary>
    public const string RuleOpeningBalance = "PP30-OPENING-BALANCE";
    /// <summary>รอบ 269 H2 — ใบเสร็จที่แปลงจากใบวางบิลรวม = รับชำระใบแจ้งหนี้ที่รวมไว้ (ไม่ใช่การขาย)</summary>
    public const string RuleRollupBillingNoteReceipt = "OUT-VAT-ROLLUP-BN-RECEIPT";
    /// <summary>รอบ 269 H2 — ใบสำคัญรับเดี่ยวที่ไม่มี VAT (สลิปรับโอน/50 ทวิ ที่เราถูกหัก) = รับเงิน ไม่ใช่การขาย</summary>
    public const string RuleZeroVatReceiptVoucher = "OUT-VAT-RV-NO-VAT";
    /// <summary>รอบ 269 H2 — ช่อง 0%/ยกเว้นรับเฉพาะใบที่ถือภาษีขายเป็นการขาย (ใบกำกับ หรือใบที่เป็นเจ้าของ VAT) ·
    /// ใบเจ้าของที่ไม่มี VAT และไม่ใช่ใบกำกับ ⇒ ไม่นับ + บรรทัดเตือนบนรายงาน (ผู้ทำบัญชีตัดสินเอง)</summary>
    public const string RuleZeroVatNotTaxInvoice = "PP30-ZERO-VAT-NOT-TAX-INVOICE";

    /// <summary>ใบนี้อยู่นอก ภ.พ.30 ทุกช่องทั้งสองฝั่งไหม (ตัวตัดสินเดียว — loop หลัก · ปุ่มดึง · ช่องยอดขาย/ซื้อ)</summary>
    public static bool IsOutsidePp30(Document d) => d.IsOpeningBalance;

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
        decimal Gl21913Net,                // ยอด Cr−Dr บน 21913 ของ JE ที่ใบนี้เป็นต้นทาง
        bool IsOpeningBalance = false,     // รอบ 269 H1 — ใบยอดยกมา
        bool RelatedIsRollupBillingNote = false);   // รอบ 269 H2 — ใบอ้างเป็นใบวางบิลรวม (BillingNoteKind.IsRollup)

    public static Facts FactsOf(Document d, bool supersededByTaxInvoice, bool ownedByVatReceipt, decimal gl21913Net,
        DocumentType? relatedType, DateTime? relatedOutputVatDueAt, bool relatedIsRollupBillingNote = false)
        => new(d.DocumentType, d.VatAmount, d.TaxPointDate ?? d.DocumentDate, d.OutputVatDueAt,
            d.RelatedDocumentId.HasValue, relatedType, relatedOutputVatDueAt,
            d.IsDeposit, d.DepositOutputVatDeferred, d.DepositAppliedToDocumentId.HasValue, d.DepositOutputVatRecognizedAt,
            supersededByTaxInvoice, ownedByVatReceipt, gl21913Net, IsOutsidePp30(d), relatedIsRollupBillingNote);

    /// <param name="Owns">ใบนี้เป็นเจ้าของภาษีขาย และ VAT ถึงกำหนดแล้ว</param>
    /// <param name="EffectivelyDeferred">มัดจำที่ VAT พัก (ธง หรือ GL 21913 ค้าง) — วันที่ถึงกำหนด = วันรับรู้</param>
    /// <param name="VatDueDate">วันที่ VAT ถึงกำหนด (ใช้หาเดือนภาษี) · null เมื่อไม่ Owns — ยกเว้น <see cref="RuleZeroVatNotTaxInvoice"/> ของ
    /// <see cref="SalesBoxOwner"/> ซึ่งคงวันที่ของใบไว้สำหรับบรรทัดเตือน</param>
    public sealed record Decision(Branch Branch, bool Owns, bool EffectivelyDeferred, DateTime? VatDueDate,
        string? RuleCode, string? Reason);

    private static Decision Own(Branch b, DateTime due, bool deferred = false) => new(b, true, deferred, due, null, null);
    private static Decision Not(Branch b, string rule, string reason, bool deferred = false) => new(b, false, deferred, null, rule, reason);

    /// <summary>สาขาของ loop หลัก (เงื่อนไขเดิมทุกตัวอักษร — เทสต์ <c>OutputVatOwnershipTests</c> เทียบกับสำเนาโค้ดเดิม ·
    /// รอบ 269 H1/H2 ปิดทางตกสามทางโดยตั้งใจ: ใบยอดยกมา · ใบเสร็จจากใบวางบิลรวม · ใบสำคัญรับเดี่ยวที่ไม่มี VAT)</summary>
    private static Branch BranchOf(Facts f)
    {
        if (f.IsOpeningBalance) return Branch.None;
        if ((f.Type is DocumentType.Receipt or DocumentType.ReceiptVoucher) && f.RelatedIsRollupBillingNote) return Branch.None;
        if (f.Type == DocumentType.ReceiptVoucher && !f.HasRelated && f.VatAmount <= 0.005m) return Branch.None;
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

    /// <summary>รอบ 269 (ผู้ใช้รายงาน ก.ย. 2569: ช่อง 0% "ส่งออก §80/1" 1,235,494.24 ทั้งที่บริษัทไม่มีขาย 0% เลย) —
    /// <b>ตัวตัดสินเดียว</b>ของ "บรรทัดของใบไหนนับเข้าช่องยอดขาย (0% / ยกเว้น) ของ ภ.พ.30"
    ///
    /// <para>เดิม <c>GenerateVatReport</c> บวก <c>Pp30SalesClassifier.Split</c> ของ<b>ทุกใบฝั่งขาย</b>ที่ออกแล้ว ⇒ ใบเสร็จรับชำระ
    /// ("รับชำระเงินตามใบกำกับภาษีเลขที่ …" บรรทัด VatRate 0) · ใบวางบิล · ใบส่งของ · ใบเสนอราคาที่อนุมัติ · ใบแจ้งหนี้ที่ออกใบกำกับแทนแล้ว ·
    /// ใบแจ้งหนี้ที่ใบเสร็จถือ VAT · มัดจำที่ถูกหักแล้ว · ลูกหนี้ยกมา (OB-AR) ถูกนับเป็นยอดขาย 0%/ยกเว้น</para>
    ///
    /// <para>กติกา (คำตัดสิน 269-1-1 · 269-2-1/2 หลังฝ่ายค้าน H1/H2): นับเฉพาะใบที่ <see cref="Decide"/> บอกว่าเป็นเจ้าของภาษีขาย (ชุดเดียวกับแถว §87 —
    /// <b>ทุกใบที่นับจึงมีแถว §87 เสมอ</b> ซึ่งเป็นเงื่อนไขที่ <c>VatReportLineKind.StandardBase</c> ต้องการ · ฝ่ายค้าน M1) <b>และ</b> ถือภาษีขายเป็นการขาย =
    /// ใบกำกับภาษี (รวมใบกำกับ 0% ล้วน) หรือใบเจ้าของที่มี VAT · ใบเจ้าของที่ไม่มี VAT และไม่ใช่ใบกำกับ (ใบเสร็จเดี่ยว VAT 0) ⇒ ไม่นับ
    /// (<see cref="RuleZeroVatNotTaxInvoice"/> — ผู้เรียกใส่บรรทัดเตือน) · ใบยอดยกมาไม่นับเสมอ · งวด = <see cref="Decision.VatDueDate"/></para>
    ///
    /// <para>ใบลดหนี้/เพิ่มหนี้ใช้ <see cref="AdjustmentNoteCounts"/></para></summary>
    public static Decision SalesBoxOwner(Facts f)
    {
        const string zeroVatReason = "ใบนี้ไม่มี VAT และไม่ใช่ใบกำกับภาษี — ยอดอัตรา 0% บนใบนี้ไม่นับเข้าช่องยอดขาย 0% (ขาย §80/1 ต้องออกใบกำกับภาษี) · ถ้าเป็นส่งออกจริงให้ออกใบกำกับ หรือถ้าเป็นขายยกเว้นให้แก้บรรทัดเป็น \"ยกเว้น\" (ผู้ทำบัญชียืนยัน)";
        var d = Decide(f);
        if (d.Owns)
            return f.Type == DocumentType.TaxInvoice || f.VatAmount > 0.005m
                ? d
                // ไม่นับ แต่คงวันที่ของการขายไว้ให้ผู้เรียกใส่บรรทัดเตือนในงวดที่ถูก (Owns=false · VatDueDate = วันที่ของใบ)
                : d with { Owns = false, RuleCode = RuleZeroVatNotTaxInvoice, Reason = zeroVatReason };
        // ใบแจ้งหนี้ที่ไม่มี VAT ทั้งใบ (ไม่ถูกใบกำกับ/ใบเสร็จแทน · ไม่ใช่ยอดยกมา) = การขายที่ไม่มีใบกำกับ ⇒ ไม่นับ + เตือนแบบเดียวกัน
        // (เดิม 269-1-2 ให้นับเอง — ถอดตามคำตัดสิน 269-2-2 เพราะยอดยกมา/ใบฝากขายอัตโนมัติก็เป็นรูปนี้)
        if (IsZeroVatSaleOrigin(f))
            return new Decision(d.Branch, false, false, f.TaxPoint, RuleZeroVatNotTaxInvoice, zeroVatReason);
        return d;
    }

    /// <summary>ใบต้นทางของการขายที่ไม่มี VAT และไม่ใช่ใบกำกับ (ไม่เป็นเจ้าของแถว §87 เพราะไม่มีภาษี) — ใบแจ้งหนี้ตรงที่ไม่ถูกใบกำกับ/ใบเสร็จถือ VAT แทน ·
    /// ใบสำคัญรับเดี่ยว (ไม่อ้างใบใด · ไม่ใช่มัดจำที่ถูกหัก) · ไม่ใช่ยอดยกมา — ใช้ร่วมกันโดย <see cref="SalesBoxOwner"/> (เตือน 0%) และ <see cref="ExemptBoxOwner"/></summary>
    private static bool IsZeroVatSaleOrigin(Facts f)
        => !f.IsOpeningBalance && f.VatAmount <= 0.005m
           && ((f.Type == DocumentType.Invoice && !f.SupersededByTaxInvoice && !f.OwnedByVatReceipt)
               || (f.Type == DocumentType.ReceiptVoucher && !f.HasRelated && !(f.IsDeposit && f.DepositApplied)));

    /// <summary>รอบ 269 คำตัดสิน 269-2-9 — <b>ช่อง 3 (ยกเว้น §81)</b>: บรรทัด VatRate −1 ของ<b>ใบต้นทางของการขาย</b> · ขายยกเว้นออกใบกำกับไม่ได้ (§81 · §86)
    /// ⇒ อยู่บนใบแจ้งหนี้/ใบเสร็จ/ใบสำคัญรับเท่านั้น จึงต้องไม่ผูกกับ "ต้องมี VAT" แบบช่อง 2 (§80/1 ต้องออกใบกำกับ ⇒ ช่อง 2 = <see cref="SalesBoxOwner"/>)
    ///
    /// <para>นับ: ใบที่ <see cref="Decide"/> เป็นเจ้าของ (ใบกำกับผสม · ใบเสร็จขายสด · ใบเสร็จถือ VAT ของใบแจ้งหนี้) + ใบต้นทางที่ไม่มี VAT (<see cref="IsZeroVatSaleOrigin"/>) ·
    /// ไม่นับ: ใบเสร็จรับชำระใบอื่น · ใบวางบิล/ใบส่งของ/ใบเสนอราคา · ยอดยกมา · ใบแจ้งหนี้ที่ถูกแทน · มัดจำที่ถูกหัก — ตัวตัดสินเดียวกับแถว §87 (Decide/BranchOf/IsOutsidePp30)</para></summary>
    public static Decision ExemptBoxOwner(Facts f)
    {
        var d = Decide(f);
        if (d.Owns) return d;
        return IsZeroVatSaleOrigin(f) ? Own(d.Branch, f.TaxPoint) : d;
    }

    /// <summary>รอบ 269 L3 — ใบลดหนี้/เพิ่มหนี้ฝั่งขายนับเข้าช่อง 0%/ยกเว้นได้เมื่อ (1) ใบเดิมถึงจุดความรับผิดแล้ว (ไม่ใช่ใบที่ VAT ยังพัก — ผู้เรียกส่ง
    /// <paramref name="relatedPendingTaxPoint"/> จากด่านเดียวกับแถว CN/DN) และ (2) ใบเดิม (ถ้ามี) เป็นใบที่ช่องยอดขายนับได้ = ใบกำกับภาษี หรือใบที่มี VAT ·
    /// ใบเดิมเป็นใบเสร็จรับชำระ/ใบวางบิล/ใบเสนอราคา/ใบยอดยกมา/ใบไม่มี VAT ⇒ ไม่นับ (ใบเดิมไม่เคยอยู่ในช่อง) · ไม่มีใบเดิม ⇒ นับตามเดิม</summary>
    /// <param name="exemptBox">true = ตัดสินช่อง 3 (ยกเว้น · 269-2-9): ใบแจ้งหนี้เดิมที่ไม่มี VAT (ขายยกเว้นตรง) นับได้ด้วย · ใบเสร็จ/ใบสำคัญรับที่ไม่มี VAT
    /// ไม่นับ (แยกใบรับชำระกับขายสดจากใบลดหนี้ไม่ได้ — ไม่เดา)</param>
    public static bool AdjustmentNoteCounts(bool hasRelated, DocumentType? relatedType, decimal relatedVatAmount,
        bool relatedOpeningBalance, bool relatedPendingTaxPoint, bool exemptBox = false)
    {
        if (relatedPendingTaxPoint) return false;
        if (!hasRelated) return true;
        if (relatedOpeningBalance || relatedType == null) return false;
        if (relatedType == DocumentType.TaxInvoice) return true;
        if (exemptBox && relatedType == DocumentType.Invoice) return true;
        return relatedType is DocumentType.Invoice or DocumentType.Receipt or DocumentType.ReceiptVoucher or DocumentType.DebitNote
            && relatedVatAmount > 0.005m;
    }

    private static Decision NotOwner(Facts f, Branch branch)
    {
        if (f.IsOpeningBalance)
            return Not(branch, RuleOpeningBalance, "ใบยอดยกมา (ลูกหนี้/เจ้าหนี้ยกมา) ไม่ใช่การขาย/การซื้อของงวดใด — ไม่เข้า ภ.พ.30");
        if ((f.Type is DocumentType.Receipt or DocumentType.ReceiptVoucher) && f.RelatedIsRollupBillingNote)
            return Not(branch, RuleRollupBillingNoteReceipt,
                "ใบเสร็จนี้แปลงจากใบวางบิลรวม = รับชำระใบแจ้งหนี้/ใบกำกับที่รวมไว้ — ภาษีขาย/ยอดขายอยู่ที่ใบต้นทางแต่ละใบ");
        if (f.Type == DocumentType.ReceiptVoucher && !f.HasRelated && f.VatAmount <= 0.005m)
            return Not(branch, RuleZeroVatReceiptVoucher,
                "ใบสำคัญรับนี้ไม่มี VAT และไม่ได้อ้างการขาย (เช่น สลิปรับโอน) — เป็นการรับเงิน ไม่ใช่ยอดขาย 0%/ยกเว้น");
        if (f.Type == DocumentType.Invoice && f.SupersededByTaxInvoice)
            return Not(branch, RuleSuperseded,
                "ออกใบกำกับภาษีอ้างใบแจ้งหนี้นี้แล้ว — ภาษีขายอยู่ที่ใบกำกับ ดึงใบกำกับแทน");
        if (f.Type is DocumentType.Receipt or DocumentType.ReceiptVoucher)
            return Not(branch, RuleSettlementOnly,
                "ใบเสร็จนี้รับชำระใบแจ้งหนี้/ใบกำกับ — ภาษีขายอยู่ที่ใบต้นทาง ไม่ใช่ใบเสร็จ");
        return Not(branch, RuleNotOutputDocument, "เอกสารนี้ไม่ได้เป็นผู้ถือภาษีขาย");
    }
}
