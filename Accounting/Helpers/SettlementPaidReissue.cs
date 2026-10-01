using System.Globalization;
using System.Reflection;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

// ═══════════════════════════════════════════════════════════════════════
// รอบ 200 ทีม V1 · คำตัดสินข้อ 9 (review198-S3 S3-5): "ยกเลิกและออกใบแทน" ของใบขายที่รอบโอน settlement ที่ลงบัญชีแล้วรับชำระไว้
//
// ปัญหาเดิม: ใบขายของผู้ใช้ที่รอบโอน Posted รับชำระเข้าผังพัก ยกเลิกทีละใบไม่ได้ (SettlementArtifactGuard.CheckDocumentPaymentsAsync)
// และถ้ารอบโอนนั้นยกเลิกการลงบัญชีไม่ได้ (ภ.พ.30 ของใบสรุปประกาศแล้ว/e-Tax ตอบรับ) ⇒ ใบกำกับที่ชื่อ/ที่อยู่ผู้ซื้อผิด
// "ยกเลิกแล้วออกใหม่" (§86/4 — ไม่มีใบลดหนี้ให้ใช้กับกรณีนี้) ทำไม่ได้เลย
//
// คำตัดสิน: ใบเดิม Voided + ReplacedByDocumentId · ใบใหม่ยอด/บรรทัด/อัตรา VAT/tax point เท่าเดิมทุกตัว (ต่างได้เฉพาะข้อมูลผู้ซื้อ/คำบรรยาย)
// · การรับชำระ + คู่จับของบรรทัดรอบโอน (+ JE ที่อ้างใบ) ชี้ใบใหม่ในธุรกรรมเดียว · รอบโอนและผังพักไม่ถูกแตะ · เงินจริงไม่เปลี่ยน ⇒ ไม่กลับรายการเงิน
// ไฟล์นี้ = ตัวตัดสินบริสุทธิ์ทั้งหมด (G6) · service หาข้อเท็จจริงแล้วทำตาม · จุดเรียกล็อกด้วย tools/required_call_site_check.py
// ═══════════════════════════════════════════════════════════════════════

/// <summary>ภาพย่อ 1 บรรทัดของเอกสาร — เฉพาะช่องที่ตัดสินว่า "ใบใหม่เท่าใบเดิม" ไหม (<see cref="SettlementPaidReissue.ForbiddenChanges"/>)</summary>
public sealed record ReissueLineSnapshot(
    int LineOrder, string? ProductCode, string? Description, decimal Quantity, string? Unit, decimal UnitPrice,
    decimal DiscountPercent, decimal DiscountAmount, decimal Amount, decimal VatRate, decimal VatAmount,
    decimal WithholdingTaxRate, decimal WithholdingTaxAmount, string? IncomeTypeCode, Guid? AccountId, Guid? ProjectId,
    bool IsVatClaimable)
{
    public static ReissueLineSnapshot Of(DocumentLine l) => new(
        l.LineOrder, l.ProductCode, l.Description, l.Quantity, l.Unit, l.UnitPrice, l.DiscountPercent, l.DiscountAmount, l.Amount,
        l.VatRate, l.VatAmount, l.WithholdingTaxRate, l.WithholdingTaxAmount, l.IncomeTypeCode, l.AccountId, l.ProjectId,
        l.IsVatClaimable);
}

/// <summary>ภาพย่อหัวเอกสาร — ยอด/วันที่/อัตรา/จุดความรับผิด + ช่องที่ "ต่างได้" (ผู้ซื้อ · หมายเหตุ)</summary>
public sealed record ReissueDocumentSnapshot(
    DocumentType Type, DateTime DocumentDate, DateTime? TaxPointDate, DateTime? OutputVatDueAt, DateTime? DeliveryDate,
    DateTime? DueDate, string Currency, decimal ExchangeRate, bool PricesIncludeVat,
    decimal SubTotal, decimal DiscountAmount, decimal BillDiscountPercent, decimal BillDiscountAmount, decimal DepositBaseDeducted,
    decimal VatAmount, decimal WithholdingTaxAmount, decimal TotalAmount, decimal PaidAmount, decimal RoundingAdjustment,
    bool? IsTaxInvoiceByLaw, string? IssuerBranchCode, Guid? BranchId, bool IsForeignService,
    Guid ContactId, string? Notes, IReadOnlyList<ReissueLineSnapshot> Lines)
{
    /// <summary>ภาพย่อจาก entity (บรรทัดที่ไม่ถูกลบ เรียงตามลำดับ)</summary>
    public static ReissueDocumentSnapshot Of(Document d, IEnumerable<DocumentLine> lines) => new(
        d.DocumentType, d.DocumentDate, d.TaxPointDate, d.OutputVatDueAt, d.DeliveryDate, d.DueDate, d.Currency, d.ExchangeRate,
        d.PricesIncludeVat, d.SubTotal, d.DiscountAmount, d.BillDiscountPercent, d.BillDiscountAmount, d.DepositBaseDeducted,
        d.VatAmount, d.WithholdingTaxAmount, d.TotalAmount, d.PaidAmount, d.RoundingAdjustment, d.IsTaxInvoiceByLaw,
        d.IssuerBranchCode, d.BranchId, d.IsForeignService, d.ContactId, d.Notes,
        lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder).Select(ReissueLineSnapshot.Of).ToList());
}

/// <summary>ใบเสร็จอัตโนมัติ 1 ใบของการรับชำระที่จะย้าย (ระบบยกเลิกแล้วออกใหม่อ้างใบใหม่) — ต้องผ่านด่านของ "ยกเลิกเอกสาร" แบบเดียวกับใบขาย
/// เพราะใบเสร็จถือ VAT (§78/1 ใบกำกับ ณ วันรับเงิน) <b>เป็นเจ้าของแถว ภ.พ.30</b> แทนใบแจ้งหนี้ (รอบ 200 ทีม V1F · ฝ่ายค้าน V1-R1)</summary>
/// <param name="StrongestEtax">สถานะ e-Tax ที่ใช้ตัดสิน (<see cref="DocumentVoidPreconditions.EffectiveEtaxAsync"/> — รวม e-Tax by Email)</param>
/// <param name="FilingLocked">ใบเสร็จนี้อยู่ในรายงานภาษีที่ยื่นและล็อกแล้ว</param>
/// <param name="ClosedPeriodName">งวดบัญชีของวันที่ใบเสร็จ (วันรับเงิน) ที่ปิดแล้ว — null = เปิด/ไม่มีงวด</param>
public sealed record ReissueReceiptFact(string? Number, EtaxStatus? StrongestEtax, bool FilingLocked, string? ClosedPeriodName);

/// <summary>ข้อเท็จจริงของใบเดิม (โหลดจากฐาน · tenant แล้ว) สำหรับ <see cref="SettlementPaidReissue.Decide"/></summary>
/// <param name="PostedBatchPaymentBlock">ผลของ <see cref="SettlementArtifactGuard.CheckDocumentPaymentsAsync"/> — null = ไม่มีการรับชำระจากรอบโอนที่ลงบัญชีแล้ว
/// (ใช้ "ยกเลิกเอกสาร" ตามปกติได้ ทางนี้ไม่เกี่ยว)</param>
/// <param name="CreatedBySettlementBatch">ใบของรอบโอนเอง (ใบสรุป/ใบค่าธรรมเนียม · ป้าย CreatedBy) — ทางที่ถูกคือยกเลิกการลงบัญชี</param>
/// <param name="DocumentEtax">สถานะ e-Tax ของใบนี้ที่ใช้ตัดสิน (<see cref="DocumentVoidPreconditions.EffectiveEtaxAsync"/>) — ถึงกรมสรรพากรแล้ว
/// (ส่ง/ตอบรับ/อีเมลประทับเวลา · <see cref="DocumentVoidPreconditions.EtaxReachedRdStatuses"/> ชุดเดียวกับใบเสร็จ) = บล็อก (รอบ 200 ทีม V1F · V1-R4 —
/// เดิมบล็อกเฉพาะ Accepted แล้วพลิก Submitted เป็น Voided ในฐานเรา)</param>
/// <param name="FilingLocked">อยู่ในรายงานภาษีที่ล็อกการยื่นแล้ว (ด่านเดียวกับ VoidDocumentAsync)</param>
/// <param name="WhtFiledBlock">50 ทวิ ของใบที่อยู่ในแบบที่ยื่นแล้ว (<see cref="WhtCertVoidGuard"/>)</param>
/// <param name="ChildBlock">เอกสารลูก/ใบลดหนี้อ้าง (<see cref="DocumentVoidPreconditions.ChildBlocksAsync"/> — ไม่นับใบเสร็จอัตโนมัติของการรับชำระที่ย้าย)</param>
/// <param name="ClosedPeriodName">งวดบัญชีของวันที่เอกสารที่ปิดแล้ว (null = เปิด/ไม่มีงวด)</param>
/// <param name="SharedPaymentNumber">การรับชำระที่จัดสรรเข้าหลายใบ (ย้ายบางส่วนไม่ได้) — null = ไม่มี</param>
public sealed record SettlementPaidReissueFacts(
    DocumentType Type, DocumentStatus Status, bool IsSettlementReceipt, bool AlreadyReplaced, bool CreatedBySettlementBatch,
    string? PostedBatchPaymentBlock, bool IsDeposit, bool HasDepositApplied,
    EtaxStatus? DocumentEtax, bool FilingLocked, string? WhtFiledBlock, string? ChildBlock, string? ClosedPeriodName,
    string? SharedPaymentNumber, int ActiveWhtCertificates, bool HasVatDeferral, IReadOnlyList<ReissueReceiptFact> Receipts);

/// <param name="Relevant">ทางนี้เกี่ยวกับใบนี้ไหม (ใบขายที่ออกแล้ว · มีการรับชำระจากรอบโอนที่ลงบัญชีแล้ว) — false = หน้าเว็บไม่แสดงปุ่มเลย</param>
/// <param name="Allowed">กดได้</param>
/// <param name="Reason">ข้อความไทยพร้อมทางไปต่อเมื่อกดไม่ได้</param>
public sealed record SettlementPaidReissueVerdict(bool Relevant, bool Allowed, string? Reason, string? RuleCode)
{
    public static SettlementPaidReissueVerdict Ok { get; } = new(true, true, null, null);
    internal static SettlementPaidReissueVerdict NotRelevant(string reason, string rule) => new(false, false, reason, rule);
    internal static SettlementPaidReissueVerdict Blocked(string reason, string rule) => new(true, false, reason, rule);
}

/// <summary>
/// **ตัวตัดสิน "ยกเลิกและออกใบแทน" ของใบขายที่รอบโอนที่ลงบัญชีแล้วรับชำระ** — ด่านสิทธิ์เข้า (<see cref="Decide"/>) ·
/// ด่าน "ใบใหม่เท่าใบเดิม" (<see cref="ForbiddenChanges"/>) · ข้อความบนใบ · ตัวคัดลอกช่องของ entity
/// </summary>
public static class SettlementPaidReissue
{
    /// <summary>ชนิดใบขายที่รอบโอนจับคู่รับชำระได้ (ชุดเดียวกับ <c>SettlementImportService.SaleDocumentTypes</c>)</summary>
    public static readonly DocumentType[] SaleTypes = { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt };

    /// <summary>ด่านชั้นแรกจากตัวเอกสารล้วน (ไม่แตะฐาน) — ไม่เกี่ยว = คืนคำตัดสิน · null = ต้องตรวจต่อด้วย <see cref="Decide"/> ·
    /// ใช้ให้หน้าเอกสาร (ทุกใบ) ไม่ต้องอ่านฐานเพิ่มสำหรับใบที่ไม่เกี่ยว</summary>
    public static SettlementPaidReissueVerdict? QuickRelevance(DocumentType type, DocumentStatus status, bool isSettlementReceipt,
        bool alreadyReplaced, bool createdBySettlementBatch)
    {
        if (!SaleTypes.Contains(type))
            return SettlementPaidReissueVerdict.NotRelevant("ยกเลิกและออกใบแทนใช้ได้เฉพาะใบขาย (ใบแจ้งหนี้ · ใบกำกับภาษี · ใบเสร็จรับเงิน)", "REISSUE-TYPE");
        if (isSettlementReceipt)
            return SettlementPaidReissueVerdict.NotRelevant(
                "ใบเสร็จนี้ออกอัตโนมัติคู่การรับชำระ — ยกเลิกและออกใบแทนที่ใบขายต้นทาง (ระบบออกใบเสร็จใหม่ให้เอง)", "REISSUE-AUTO-RECEIPT");
        if (!DocumentStatusRules.IsEffective(status))
            return SettlementPaidReissueVerdict.NotRelevant("ใช้ได้เฉพาะเอกสารที่ออกแล้วและยังไม่ถูกยกเลิก", "REISSUE-STATUS");
        if (alreadyReplaced)
            return SettlementPaidReissueVerdict.NotRelevant("เอกสารนี้ถูกออกใบแทนไปแล้ว", "REISSUE-ALREADY");
        if (createdBySettlementBatch)
            return SettlementPaidReissueVerdict.NotRelevant(
                "ใบนี้ระบบสร้างจากการลงบัญชีรอบโอน — แก้ด้วย “ยกเลิกการลงบัญชี” ของรอบโอน หรือใบลดหนี้/ใบเพิ่มหนี้", "REISSUE-SETTLEMENT-ARTIFACT");
        return null;
    }

    public static SettlementPaidReissueVerdict Decide(SettlementPaidReissueFacts f)
    {
        if (QuickRelevance(f.Type, f.Status, f.IsSettlementReceipt, f.AlreadyReplaced, f.CreatedBySettlementBatch) is { } quick)
            return quick;
        if (f.PostedBatchPaymentBlock == null)
            return SettlementPaidReissueVerdict.NotRelevant(
                "ใบนี้ไม่มีการรับชำระจากรอบโอนที่ลงบัญชีแล้ว — ใช้ “ยกเลิกเอกสาร” ตามปกติแล้วออกใบใหม่", "REISSUE-NOT-NEEDED");

        const string Tail = " · ระบบยังไม่ได้แตะอะไร";
        if (f.IsDeposit || f.HasDepositApplied)
            return SettlementPaidReissueVerdict.Blocked(
                "ใบนี้เกี่ยวกับเงินมัดจำ (เป็นใบมัดจำ หรือมีมัดจำตัดชำระเข้าใบนี้) — ยกเลิกและออกใบแทนยังไม่รองรับ (ต้องย้ายรายการตัดมัดจำด้วย) · "
                + "ถ้าผิดเฉพาะข้อมูลผู้ซื้อ ให้แจ้งผู้ดูแลระบบ" + Tail, "REISSUE-DEPOSIT");
        if (f.DocumentEtax == EtaxStatus.Accepted)
            return SettlementPaidReissueVerdict.Blocked(
                "e-Tax ของใบนี้ถึงกรมสรรพากรแล้วและยกเลิกจากระบบนี้ไม่ได้ (ตอบรับแล้ว หรือส่งแบบ e-Tax by Email ที่ประทับเวลาแล้ว) — "
                + "ต้องยกเลิก/ออกแทนที่ระบบ e-Tax ของกรมสรรพากรก่อน (ระบบนี้ยังไม่มีช่องทางส่งใบแทน e-Tax · ด่านเดียวกับ “ยกเลิกเอกสาร”)" + Tail,
                "REISSUE-ETAX-ACCEPTED");
        // V1-R4: ส่งแล้วยังไม่รู้ผล = ถึงกรมสรรพากรแล้ว (ชุดสถานะเดียวกับใบเสร็จ) — เดิมพลิกเป็น Voided ในฐานเรา แล้ว RD ตอบรับทั้งใบเดิมและใบใหม่ได้
        if (DocumentVoidPreconditions.EtaxReachedRd(f.DocumentEtax))
            return SettlementPaidReissueVerdict.Blocked(
                "e-Tax ของใบนี้ส่งไปกรมสรรพากรแล้วแต่ยังไม่รู้ผล (Submitted) — เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบนี้ก่อน "
                + "(ทำได้ก่อนกรมสรรพากรตอบรับ · ต้องแนบไฟล์หลักฐานการยกเลิกจากกรมสรรพากร/ผู้ให้บริการ e-Tax) แล้วกดอีกครั้ง · ถ้ากรมสรรพากรตอบรับแล้ว ต้องยกเลิก/ออกแทนที่ระบบ e-Tax ของกรมสรรพากร" + Tail,
                "REISSUE-ETAX-SUBMITTED");
        if (f.FilingLocked)
            return SettlementPaidReissueVerdict.Blocked(
                "เอกสารนี้อยู่ในรายงานภาษีที่ยื่นและล็อกแล้ว — ปลดล็อกรายงาน (ผู้ดูแล) หรือใช้ “Reject & Reverse” ของรายงานก่อน "
                + "(ด่านเดียวกับ “ยกเลิกเอกสาร”)" + Tail, "REISSUE-FILING-LOCKED");
        if (f.WhtFiledBlock != null)
            return SettlementPaidReissueVerdict.Blocked(f.WhtFiledBlock + Tail, "RD-50TWI-FILED");
        if (f.ChildBlock != null)
            return SettlementPaidReissueVerdict.Blocked(f.ChildBlock + Tail, "REISSUE-CHILD");
        if (f.ClosedPeriodName != null)
            return SettlementPaidReissueVerdict.Blocked(
                $"งวดบัญชี {f.ClosedPeriodName} ของวันที่เอกสารปิดแล้ว — เปิดงวดก่อน (ใบแทนใช้วันที่เดิม · ลูกหนี้รายผู้ซื้อของงวดนั้นจะเปลี่ยน)" + Tail,
                "REISSUE-PERIOD-CLOSED");
        if (f.SharedPaymentNumber != null)
            return SettlementPaidReissueVerdict.Blocked(
                $"การรับชำระ {f.SharedPaymentNumber} จัดสรรเข้าหลายเอกสาร — ย้ายบางส่วนไปใบใหม่ไม่ได้ · ยกเลิกการชำระนั้นทั้งใบแล้วจัดสรรใหม่ก่อน" + Tail,
                "REISSUE-SHARED-PAYMENT");
        if (f.ActiveWhtCertificates > 0)
            return SettlementPaidReissueVerdict.Blocked(
                "ใบนี้มีหนังสือรับรอง 50 ทวิ ผูกอยู่ — ยกเลิก/จัดการ 50 ทวิ ก่อน (ยกเลิกและออกใบแทนไม่ย้าย 50 ทวิ)" + Tail, "REISSUE-WHT-CERT");
        if (f.HasVatDeferral)
            return SettlementPaidReissueVerdict.Blocked(
                "ใบนี้มีรายการเลื่อนภาษีข้ามงวดผูกกับรายงานภาษี — จัดการที่รายงานภาษีก่อน" + Tail, "REISSUE-VAT-DEFERRAL");
        foreach (var r in f.Receipts)
        {
            // V1-R1: ใบเสร็จถือ VAT เป็นเจ้าของแถว ภ.พ.30 (ใบแจ้งหนี้บริการ §78/1 ถูกข้าม) ⇒ ยกเลิก+ออกเลขใหม่ในงวดที่ล็อก = แบบที่ยื่นอ้างใบที่ถูกยกเลิก
            if (r.FilingLocked)
                return SettlementPaidReissueVerdict.Blocked(
                    $"ใบเสร็จ {r.Number} ที่ออกคู่การรับชำระ (ใบกำกับภาษี ณ วันรับเงิน §78/1) อยู่ในรายงานภาษีที่ยื่นและล็อกแล้ว — ยกเลิกและออกใบแทน"
                    + "ต้องยกเลิกใบเสร็จนั้นและออกเลขใหม่ในเดือนที่ยื่นแล้ว · ปลดล็อกรายงาน (ผู้ดูแล) หรือใช้ “Reject & Reverse” ของรายงานก่อน "
                    + "(ด่านเดียวกับ “ยกเลิกเอกสาร”) · หรือถ้าผิดเฉพาะยอด ให้ออกใบลดหนี้/ใบเพิ่มหนี้ในเดือนปัจจุบันแทน" + Tail,
                    "REISSUE-RECEIPT-FILING-LOCKED");
            if (r.ClosedPeriodName != null)
                return SettlementPaidReissueVerdict.Blocked(
                    $"งวดบัญชี {r.ClosedPeriodName} ของวันที่ใบเสร็จ {r.Number} (วันรับเงิน) ปิดแล้ว — เปิดงวดก่อน "
                    + "(ระบบยกเลิกใบเสร็จนั้นและออกใหม่ลงวันรับเงินเดิม)" + Tail,
                    "REISSUE-RECEIPT-PERIOD-CLOSED");
            var d = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(r.StrongestEtax, r.Number, PaymentVoidCause.User);
            if (d.Action != AutoReceiptEtaxAction.Void)
                return SettlementPaidReissueVerdict.Blocked(
                    $"ใบเสร็จ {r.Number} ที่ออกคู่การรับชำระต้องถูกยกเลิกและออกใหม่อ้างใบใหม่ แต่ใบเสร็จนั้นส่ง e-Tax ไปกรมสรรพากรแล้ว — "
                    + (r.StrongestEtax == EtaxStatus.Accepted
                        ? "ต้องยกเลิก/ออกแทนที่ระบบ e-Tax ของกรมสรรพากรก่อน"
                        : "เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบเสร็จนั้นก่อน (ทำได้ก่อนกรมสรรพากรตอบรับ · ต้องแนบไฟล์หลักฐานการยกเลิกจากกรมสรรพากร/ผู้ให้บริการ e-Tax) แล้วกดอีกครั้ง") + Tail,
                    "REISSUE-RECEIPT-ETAX");
        }
        return SettlementPaidReissueVerdict.Ok;
    }

    /// <summary>
    /// **ตาข่าย "ใบใหม่เท่าใบเดิม"** — คืนรายการสิ่งที่ต่างซึ่ง<b>ห้ามต่าง</b> (ว่าง = ผ่าน) · ต่างได้เฉพาะผู้ซื้อ (<c>ContactId</c>) · หมายเหตุ ·
    /// คำบรรยายรายบรรทัด · ยอด/บรรทัด/อัตรา VAT/วันที่/tax point ต่าง = ต้องใช้ใบลดหนี้/ใบเพิ่มหนี้ (คำตัดสินข้อ 9) · G6: pure
    /// <para>รอบ 200 ทีม V1F (ฝ่ายค้าน V1-R8): <b>ไม่ใช่ด่านของคำขอผู้ใช้</b> — คำขอมีรูปร่างที่แก้ยอดไม่ได้อยู่แล้ว (ผู้ซื้อ · หมายเหตุ · คำบรรยาย) และ
    /// ถูกตรวจที่ <see cref="RequestLineIssues"/> · ตัวนี้ตรวจ<b>ผลของตัวคัดลอกแบบ allowlist</b> (<see cref="CopyDocumentForReissue"/>) ว่าไม่ได้ทำช่องเงิน/ภาษี/วันที่หล่น
    /// (ช่องใหม่ที่เพิ่มใน entity แล้วลืมจัดกลุ่ม = เทสต์จัดกลุ่มล้ม · ช่องในภาพย่อที่ไม่ถูกคัดลอก = ตัวนี้ฟ้องก่อนเขียน)</para>
    /// </summary>
    public static IReadOnlyList<string> ForbiddenChanges(ReissueDocumentSnapshot before, ReissueDocumentSnapshot after)
    {
        var diffs = new List<string>();
        void Cmp<T>(string label, T a, T b)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b)) diffs.Add($"{label}: {Show(a)} → {Show(b)}");
        }
        Cmp("ชนิดเอกสาร", before.Type, after.Type);
        Cmp("วันที่เอกสาร", before.DocumentDate, after.DocumentDate);
        Cmp("จุดความรับผิด (tax point)", before.TaxPointDate, after.TaxPointDate);
        Cmp("วันที่ภาษีขายถึงกำหนด (§78/1)", before.OutputVatDueAt, after.OutputVatDueAt);
        Cmp("วันส่งมอบ", before.DeliveryDate, after.DeliveryDate);
        Cmp("วันครบกำหนด", before.DueDate, after.DueDate);
        Cmp("สกุลเงิน", before.Currency, after.Currency);
        Cmp("อัตราแลกเปลี่ยน", before.ExchangeRate, after.ExchangeRate);
        Cmp("ราคารวม VAT", before.PricesIncludeVat, after.PricesIncludeVat);
        Cmp("ยอดก่อนภาษี", before.SubTotal, after.SubTotal);
        Cmp("ส่วนลด", before.DiscountAmount, after.DiscountAmount);
        Cmp("ส่วนลดท้ายบิล (%)", before.BillDiscountPercent, after.BillDiscountPercent);
        Cmp("ส่วนลดท้ายบิล", before.BillDiscountAmount, after.BillDiscountAmount);
        Cmp("หักมูลค่ามัดจำ", before.DepositBaseDeducted, after.DepositBaseDeducted);
        Cmp("ภาษีมูลค่าเพิ่ม", before.VatAmount, after.VatAmount);
        Cmp("ภาษีหัก ณ ที่จ่าย", before.WithholdingTaxAmount, after.WithholdingTaxAmount);
        Cmp("ยอดรวมทั้งสิ้น", before.TotalAmount, after.TotalAmount);
        Cmp("ยอดรับชำระ", before.PaidAmount, after.PaidAmount);
        Cmp("ผลต่างปัดเศษ", before.RoundingAdjustment, after.RoundingAdjustment);
        Cmp("บทบาทใบกำกับภาษี", before.IsTaxInvoiceByLaw, after.IsTaxInvoiceByLaw);
        Cmp("รหัสสาขาผู้ออก", before.IssuerBranchCode, after.IssuerBranchCode);
        Cmp("สาขา", before.BranchId, after.BranchId);
        Cmp("บริการต่างประเทศ", before.IsForeignService, after.IsForeignService);
        if (before.Lines.Count != after.Lines.Count)
        {
            diffs.Add($"จำนวนบรรทัด: {before.Lines.Count} → {after.Lines.Count}");
            return diffs;
        }
        for (var i = 0; i < before.Lines.Count; i++)
        {
            var a = before.Lines[i];
            var b = after.Lines[i];
            var at = $"บรรทัด {i + 1} ";
            Cmp(at + "ลำดับ", a.LineOrder, b.LineOrder);
            Cmp(at + "รหัสสินค้า", a.ProductCode, b.ProductCode);
            Cmp(at + "จำนวน", a.Quantity, b.Quantity);
            Cmp(at + "หน่วย", a.Unit, b.Unit);
            Cmp(at + "ราคาต่อหน่วย", a.UnitPrice, b.UnitPrice);
            Cmp(at + "ส่วนลด (%)", a.DiscountPercent, b.DiscountPercent);
            Cmp(at + "ส่วนลด", a.DiscountAmount, b.DiscountAmount);
            Cmp(at + "จำนวนเงิน", a.Amount, b.Amount);
            Cmp(at + "อัตรา VAT", a.VatRate, b.VatRate);
            Cmp(at + "VAT", a.VatAmount, b.VatAmount);
            Cmp(at + "อัตราหัก ณ ที่จ่าย", a.WithholdingTaxRate, b.WithholdingTaxRate);
            Cmp(at + "หัก ณ ที่จ่าย", a.WithholdingTaxAmount, b.WithholdingTaxAmount);
            Cmp(at + "ประเภทเงินได้", a.IncomeTypeCode, b.IncomeTypeCode);
            Cmp(at + "ผังบัญชี", a.AccountId, b.AccountId);
            Cmp(at + "โครงการ", a.ProjectId, b.ProjectId);
            Cmp(at + "เคลมภาษีได้", a.IsVatClaimable, b.IsVatClaimable);
            if (string.IsNullOrWhiteSpace(b.Description))
                diffs.Add(at + "คำบรรยายว่าง (§86/4 ทุกบรรทัดต้องมีรายการ)");
        }
        return diffs;
    }

    /// <summary>สิ่งที่เปลี่ยน (ที่อนุญาต) — ลง audit/หมายเหตุภายใน · ว่าง = ออกใบแทนด้วยข้อมูลเดิม (เช่น แก้ทะเบียนผู้ติดต่อแล้ว)</summary>
    public static IReadOnlyList<string> AllowedChanges(ReissueDocumentSnapshot before, ReissueDocumentSnapshot after)
    {
        var list = new List<string>();
        if (before.ContactId != after.ContactId) list.Add("ผู้ซื้อ");
        if (!string.Equals(before.Notes ?? "", after.Notes ?? "", StringComparison.Ordinal)) list.Add("หมายเหตุ");
        var n = Math.Min(before.Lines.Count, after.Lines.Count);
        for (var i = 0; i < n; i++)
            if (!string.Equals(before.Lines[i].Description ?? "", after.Lines[i].Description ?? "", StringComparison.Ordinal))
                list.Add($"คำบรรยายบรรทัด {i + 1}");
        return list;
    }

    /// <summary>หมายเหตุที่<b>พิมพ์บนใบใหม่</b> — ผู้ซื้อและผู้สอบบัญชีต้องเห็นว่าใบนี้แทนใบไหนและเพราะอะไร (แนวปฏิบัติกรมสรรพากร: ใบกำกับฉบับใหม่ลงวันที่เดิม
    /// และหมายเหตุ "ยกเลิกและออกฉบับใหม่แทนฉบับเดิม เลขที่ …" พร้อมเหตุที่ยกเลิก)</summary>
    internal static string ReplacementNote(string originalNumber, DateTime originalDate, string reason)
        => $"{ReplacementNoteMarker}“{originalNumber}” ลงวันที่ {ThaiDate.ToThaiDisplayString(originalDate)} · เหตุที่ยกเลิก: {reason.Trim()}";

    /// <summary>ขึ้นต้นของบรรทัดอ้างใบเดิม — ตัวตัดบรรทัดเก่าออก (<see cref="StripReplacementNote"/>) ใช้ค่าเดียวกับตัวสร้าง</summary>
    internal const string ReplacementNoteMarker = "ยกเลิกและออกฉบับใหม่แทนฉบับเดิม เลขที่ ";

    /// <summary>หมายเหตุ "ของผู้ใช้" (ไม่รวมบรรทัดอ้างใบเดิมที่ระบบต่อท้าย) — รอบ 200 ทีม V1F (V1-R8): ใบแทนของใบแทนเคยสะสมบรรทัดอ้างใบเดิมสองบรรทัด
    /// และ audit จด "หมายเหตุเปลี่ยน" ทุกครั้งเพราะเทียบรวมบรรทัดที่ระบบต่อเอง · บรรทัดอ้างใบเดิมอยู่ท้ายเสมอ (<see cref="ComposeNotes"/>) ⇒ ตัดจากจุดนั้น</summary>
    public static string? StripReplacementNote(string? notes)
    {
        if (string.IsNullOrEmpty(notes)) return notes;
        var at = notes.IndexOf(ReplacementNoteMarker, StringComparison.Ordinal);
        if (at < 0) return notes;
        var head = notes[..at].TrimEnd();
        if (head.EndsWith("·", StringComparison.Ordinal)) head = head[..^1].TrimEnd();
        return head.Length == 0 ? null : head;
    }

    /// <summary>หมายเหตุพิมพ์บนใบใหม่ = หมายเหตุของผู้ใช้ (หรือของใบเดิม — ตัดบรรทัดอ้างใบเก่าออกก่อน) + บรรทัดอ้างใบเดิมบรรทัดเดียว</summary>
    public static string ComposeNotes(string? notes, string originalNumber, DateTime originalDate, string reason)
    {
        var note = ReplacementNote(originalNumber, originalDate, reason);
        var baseNotes = StripReplacementNote(notes)?.Trim();
        if (string.IsNullOrEmpty(baseNotes)) return note;
        return baseNotes + " · " + note;
    }

    /// <summary>หมายเหตุภายในบนใบเดิม (ไม่พิมพ์)</summary>
    public static string OriginalNote(string replacementNumber, string reason, int movedPayments)
        => $"[VOID-REISSUE] ยกเลิกและออกใบแทน “{replacementNumber}” — ย้ายการรับชำระ {movedPayments} รายการ รายการบัญชี และคู่จับของรอบโอนไปใบใหม่ "
           + $"(ยอด/อัตรา/วันที่เท่าเดิม · เงินไม่ถูกกลับรายการ) · เหตุผล: {reason}";

    /// <summary>หมายเหตุภายในบนใบใหม่ (ไม่พิมพ์)</summary>
    public static string ReplacementInternalNote(string originalNumber, string reason, IReadOnlyList<string> changed)
        => $"[VOID-REISSUE] ออกแทน “{originalNumber}” (ยกเลิกแล้ว) — รับการรับชำระ รายการบัญชี และคู่จับของรอบโอนมาจากใบเดิม · เปลี่ยน: "
           + (changed.Count == 0 ? "ไม่มี (ข้อมูลเดิม)" : string.Join(", ", changed)) + $" · เหตุผล: {reason}";

    /// <summary>คำบรรยาย JE ที่ย้ายมาอยู่ใบใหม่ (V1-R9) — JE ยังอ้างเลขที่ใบเดิมใน <c>Reference</c> (ตัวเลือก JE ของการรับชำระ/มัดจำอ่านช่องนั้น — ห้ามแตะ)
    /// ⇒ ต่อท้ายคำบรรยายให้ผู้สอบบัญชีเห็นว่า JE นี้เป็นของใบแทนแล้ว ไม่ใช่ JE ของใบที่ยกเลิกโดยไม่มีรายการกลับ · ไม่ต่อซ้ำ</summary>
    public static string JournalDescriptionAfterMove(string? description, string originalNumber, string replacementNumber)
    {
        var tag = $"[ย้ายไปใบแทน {replacementNumber} — ใบเดิม {originalNumber} ยกเลิกและออกใบแทน]";
        var d = description?.Trim() ?? "";
        if (d.Contains(tag, StringComparison.Ordinal)) return d;
        return d.Length == 0 ? tag : d + " " + tag;
    }

    /// <summary>
    /// **ตรวจคำขอแก้คำบรรยายรายบรรทัด** (รอบ 200 ทีม V1F · V1-R8 — ด่านจริงของ "สิ่งที่ผู้ใช้ส่งมา") — อ้าง Id บรรทัดของใบเดิมเท่านั้น ·
    /// คำบรรยายที่ส่งมาต้องไม่ว่าง (§86/4 ทุกบรรทัดต้องมีรายการ) · ว่าง = ผ่าน · G6: pure
    /// </summary>
    public static IReadOnlyList<string> RequestLineIssues(IReadOnlyCollection<Guid> originalLineIds,
        IEnumerable<(Guid LineId, string? Description)> requested)
    {
        var issues = new List<string>();
        foreach (var (lineId, description) in requested)
        {
            if (!originalLineIds.Contains(lineId))
                issues.Add("มีบรรทัดที่อ้างถึงแต่ไม่อยู่ในเอกสารนี้ — โหลดหน้าเอกสารใหม่แล้วลองอีกครั้ง");
            else if (description != null && string.IsNullOrWhiteSpace(description))
                issues.Add("คำบรรยายรายการว่าง (§86/4 ทุกบรรทัดต้องมีรายการ) — พิมพ์คำบรรยาย หรือไม่แก้บรรทัดนั้น");
        }
        return issues.Distinct().ToList();
    }

    /// <summary>สถานะของใบใหม่ — การรับชำระย้ายมาทั้งหมดจึงสถานะการชำระตามเดิม · "ส่งแล้ว" ไม่ตามมา (ใบใหม่ยังไม่ถูกส่งให้ลูกค้า)</summary>
    public static DocumentStatus ReplacementStatus(DocumentStatus original)
        => original == DocumentStatus.Sent ? DocumentStatus.Approved : original;

    // ═══ V1-R7: ตัวคัดลอกแบบ allowlist — ห้ามพาหลักฐาน/สถานะของใบเดิม (ลายเซ็นรับของ · ผลตรวจ RD · e-Tax · เลข · โทเคน · feedback AI) ═══

    /// <summary>ช่องของเอกสารที่ "ตามไป" ใบแทน — เนื้อหาการขาย ยอด วันที่ จุดความรับผิด ผู้ซื้อ (ตั้งใหม่ได้) และค่าการพิมพ์ ·
    /// <c>Reference</c> (เลขอ้างอิงภายนอก/ใบสั่งซื้อของลูกค้า) <b>ตามไป</b> — ใบแทนคือการขายเดียวกัน ทางเข้าภายนอกที่ค้นด้วยเลขอ้างอิง
    /// ต้องเจอใบที่ยังมีผล (คิวรีต้องกรอง Voided + เรียงเอง · V1-R5)</summary>
    public static readonly IReadOnlyList<string> DocumentCarriedFields = new[]
    {
        nameof(Document.CompanyId), nameof(Document.DocumentType), nameof(Document.DocumentDate), nameof(Document.DueDate),
        nameof(Document.SupplierInvoiceNumber), nameof(Document.SupplierTaxInvoiceDate), nameof(Document.HasTaxInvoiceReference),
        nameof(Document.SupplierBranchCode), nameof(Document.InputVatPostedAsUndue), nameof(Document.InputVatBecameClaimableAt),
        nameof(Document.OutputVatDueAt), nameof(Document.InputVatExpiredAt), nameof(Document.CombinedInvoiceTaxInvoice),
        nameof(Document.BuyerDeclinedTaxInvoice), nameof(Document.ServedAsReceipt), nameof(Document.DocumentLanguage),
        nameof(Document.SettlesTaxInvoiceSource), nameof(Document.AdjustmentOriginalNumber), nameof(Document.AdjustmentOriginalDate),
        nameof(Document.AdjustmentOriginalSubTotal), nameof(Document.AdjustmentOriginalOurNumber), nameof(Document.AdjustmentOriginalHasVat),
        nameof(Document.IssuedAsCashReceipt), nameof(Document.IsTaxInvoiceByLaw), nameof(Document.PaidOnIssue),
        nameof(Document.InputVatAccountCodeOverride), nameof(Document.IsDeposit), nameof(Document.DepositRealizedAmount),
        nameof(Document.DepositRealizedAt), nameof(Document.DepositDeferredAccountCode), nameof(Document.DepositOutputVatDeferred),
        nameof(Document.DepositAppliedAmount), nameof(Document.DepositAppliedRef), nameof(Document.DepositAppliedDrivesJournal),
        nameof(Document.IsSettlementReceipt), nameof(Document.SettlementPaymentId), nameof(Document.DepositRefundedAmount),
        nameof(Document.DepositRefundedAt), nameof(Document.DepositRefundReason), nameof(Document.DepositAppliedToDocumentId),
        nameof(Document.BookingNumber), nameof(Document.DepositOutputVatRecognizedAt), nameof(Document.DepositKindId),
        nameof(Document.DepositNature), nameof(Document.DepositKindName), nameof(Document.DepositPolicyNote), nameof(Document.CreditDays),
        nameof(Document.PaymentTerms), nameof(Document.PaymentType), nameof(Document.PricesIncludeVat), nameof(Document.BrandId),
        nameof(Document.BranchId), nameof(Document.IssuerBranchCode), nameof(Document.DocumentTemplateId), nameof(Document.ContactId),
        nameof(Document.Reference), nameof(Document.RelatedDocumentId), nameof(Document.CnDnPurchaseSideOverride),
        nameof(Document.CreditNoteReason), nameof(Document.DebitNoteReason), nameof(Document.ProjectId), nameof(Document.DimensionId),
        nameof(Document.BankAccountId), nameof(Document.PaymentAccountId), nameof(Document.ExpenseCategoryId), nameof(Document.Currency),
        nameof(Document.ExchangeRate), nameof(Document.SubTotal), nameof(Document.DiscountAmount), nameof(Document.BillDiscountPercent),
        nameof(Document.BillDiscountAmount), nameof(Document.DepositBaseDeducted), nameof(Document.VatAmount),
        nameof(Document.WithholdingTaxAmount), nameof(Document.TotalAmount), nameof(Document.PaidAmount), nameof(Document.BalanceDue),
        nameof(Document.RoundingAdjustment), nameof(Document.ActualPaidAmount), nameof(Document.Notes), nameof(Document.OriginModule),
        nameof(Document.WhtCertSkipped), nameof(Document.Sensitivity), nameof(Document.CustomAppendix), nameof(Document.CustomFooterNotes),
        nameof(Document.CustomTermsAndConditions), nameof(Document.RevenueContractId), nameof(Document.PerformanceObligationId),
        nameof(Document.CertificateReason), nameof(Document.CertifierName), nameof(Document.CertifierPosition), nameof(Document.WitnessName),
        nameof(Document.WitnessPosition), nameof(Document.PaymentDate), nameof(Document.IsForeignService),
        nameof(Document.EarlyPaymentDiscountTermId),
        nameof(Document.DeliveryDate), nameof(Document.OwnershipTransferDate), nameof(Document.ServiceUsedDate),
        nameof(Document.CustomsDutyPaidDate), nameof(Document.TaxPointDate), nameof(Document.RetentionUntil),
        nameof(Document.NonDeductibleAmount), nameof(Document.NonDeductibleRuleJson), nameof(Document.LateReason),
        nameof(Document.IsOpeningBalance),
    };

    /// <summary>ช่องของเอกสารที่ "ไม่ตามไป" — ตัวตน/เวลา/เลข · สถานะ (ตั้งด้วย <see cref="ReplacementStatus"/>) · หลักฐานของลูกค้าเดิม (ลายเซ็นรับของ ·
    /// ตอบรับใบเสนอราคา) · โทเคน · ผลตรวจ RD ของผู้ซื้อเดิม · feedback AI (สองใบชี้แถวเดียว) · ธงของรอบโอน/ใบแทน/e-Tax · aging/ทวงหนี้ · OCR ·
    /// เลขรับ ภ.พ.36 · คำขอออกใบแทนที่ค้าง · หมายเหตุภายใน (ตั้งใหม่)</summary>
    public static readonly IReadOnlyList<string> DocumentNotCarriedFields = new[]
    {
        nameof(Document.Id), nameof(Document.CreatedAt), nameof(Document.UpdatedAt), nameof(Document.CreatedBy), nameof(Document.UpdatedBy),
        nameof(Document.IsDeleted), nameof(Document.DocumentNumber), nameof(Document.Status),
        nameof(Document.QuotationAcceptToken), nameof(Document.QuotationAcceptTokenExpiresAt), nameof(Document.QuotationAcceptedAt),
        nameof(Document.QuotationAcceptedBy), nameof(Document.RevisionNumber), nameof(Document.DeliverySignToken),
        nameof(Document.DeliverySignTokenExpiresAt), nameof(Document.DeliverySignedAt), nameof(Document.DeliverySignedBy),
        nameof(Document.DeliverySignatureBase64), nameof(Document.SettlementOrphanAckAt), nameof(Document.SettlementOrphanAckBy),
        nameof(Document.SettlementOrphanAckReason), nameof(Document.ReplacedByDocumentId), nameof(Document.ReplacesDocumentId),
        nameof(Document.ReplacementReason), nameof(Document.ReplacedAt), nameof(Document.ReplacementCarriesPostings),
        nameof(Document.EtaxCancelRequiredAt), nameof(Document.EtaxCancelRequiredReason), nameof(Document.InternalNotes),
        nameof(Document.WhtAdviceAiFeedbackId), nameof(Document.WhtAdviceAnswer), nameof(Document.WhtAdviceUsedAi),
        nameof(Document.Pp36RdReceiptNumber), nameof(Document.Pp36RdReceiptDate), nameof(Document.OcrIntelTrainedAt),
        nameof(Document.OcrConfidenceScore), nameof(Document.RdComplianceStatus), nameof(Document.RdComplianceIssuesJson),
        nameof(Document.OcrTenantMismatchFlag), nameof(Document.AgingDays), nameof(Document.AgingLastEvaluatedAt),
        nameof(Document.LastDunningSentAt), nameof(Document.LastDunningLevel), nameof(Document.ReissueRequestedAt),
        nameof(Document.ReissueRequestedBy), nameof(Document.ReissueRequestJson),
        // รอบ 200 ทีม V1G: RV1F-12 — ผู้จัดทำใบแทนคือผู้ขอ (CreatedBy) ไม่ใช่ผู้จัดทำภายนอกของใบเดิม ⇒ ลายเซ็นผู้จัดทำใบเดิมไม่ตามไป ·
        // ใบลดหนี้ที่ปิดธง e-Tax ของใบเสร็จ (ข้อ 47) เป็นของใบเสร็จนั้นเท่านั้น
        nameof(Document.PreparerName), nameof(Document.PreparerSignatureBase64), nameof(Document.EtaxCancelledByCreditNoteId),
    };

    /// <summary>ช่องของบรรทัดที่ตามไป — ทุกอย่างของรายการ ยกเว้นตัวตน/เวลา/ใบแม่ และ feedback AI ของผังบัญชี (สองบรรทัดชี้แถวเดียว)</summary>
    public static readonly IReadOnlyList<string> LineCarriedFields = new[]
    {
        nameof(DocumentLine.LineOrder), nameof(DocumentLine.ProductCode), nameof(DocumentLine.Description), nameof(DocumentLine.Quantity),
        nameof(DocumentLine.Unit), nameof(DocumentLine.UnitPrice), nameof(DocumentLine.DiscountPercent), nameof(DocumentLine.DiscountAmount),
        nameof(DocumentLine.Amount), nameof(DocumentLine.VatRate), nameof(DocumentLine.VatAmount), nameof(DocumentLine.WithholdingTaxRate),
        nameof(DocumentLine.WithholdingTaxAmount), nameof(DocumentLine.IncomeTypeCode), nameof(DocumentLine.AccountId),
        nameof(DocumentLine.ProjectId), nameof(DocumentLine.SourceLineId), nameof(DocumentLine.SourceDocumentId),
        nameof(DocumentLine.IsVatClaimable), nameof(DocumentLine.IsLandedCost), nameof(DocumentLine.VatNonClaimableReason),
    };

    /// <summary>ช่องของบรรทัดที่ไม่ตามไป</summary>
    public static readonly IReadOnlyList<string> LineNotCarriedFields = new[]
    {
        nameof(DocumentLine.Id), nameof(DocumentLine.CreatedAt), nameof(DocumentLine.UpdatedAt), nameof(DocumentLine.CreatedBy),
        nameof(DocumentLine.UpdatedBy), nameof(DocumentLine.IsDeleted), nameof(DocumentLine.DocumentId),
        nameof(DocumentLine.GlAccountAiFeedbackId),
    };

    /// <summary>คัดลอกเฉพาะ <see cref="DocumentCarriedFields"/> — ช่องอื่นของใบใหม่คงค่าเริ่มต้นของ entity (ผู้เรียกตั้งตัวตน/เลข/สถานะเอง)</summary>
    public static void CopyDocumentForReissue(Document from, Document to) => CopyNamed(from, to, DocumentCarriedFields);

    /// <summary>คัดลอกเฉพาะ <see cref="LineCarriedFields"/></summary>
    public static void CopyLineForReissue(DocumentLine from, DocumentLine to) => CopyNamed(from, to, LineCarriedFields);

    /// <summary>ช่องค่า (value type · enum · Nullable · string) ที่อ่าน/เขียนได้ทั้งหมดของชนิดนั้น — เทสต์ใช้ยืนยันว่าทุกช่องถูกจัดกลุ่ม
    /// (ตามไป/ไม่ตามไป) ครบ ⇒ ช่องใหม่ที่เพิ่มใน entity ทีหลังต้องถูกตัดสินอย่างตั้งใจ ไม่ใช่ตามไปเงียบ ๆ</summary>
    internal static IReadOnlyList<string> ScalarProperties<T>() where T : class
        => typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetSetMethod() != null)
            .Where(p =>
            {
                var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                return t.IsValueType || t == typeof(string);
            })
            .Select(p => p.Name)
            .ToList();

    private static void CopyNamed<T>(T from, T to, IEnumerable<string> names) where T : class
    {
        foreach (var name in names)
        {
            var p = typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"ไม่พบช่อง {name} บน {typeof(T).Name} (รายการคัดลอกของใบแทนล้าสมัย)");
            p.SetValue(to, p.GetValue(from));
        }
    }

    // ═══ V1-R5: ทางเข้าภายนอกสั่งยกเลิกด้วยอ้างอิงของใบที่ถูกแทนแล้ว ═══

    /// <summary>
    /// **ผลของคำสั่ง "ยกเลิกเอกสาร" จากทางเข้าภายนอก** (integration <c>document.voided</c>) เมื่อใบที่พบถูกยกเลิกแล้ว — ใบที่ถูก "ยกเลิกและออกใบแทน"
    /// (ใบแทนถือผลทางบัญชีและยังมีผล) ⇒ <b>บอกความจริง</b>: ใบเดิมถูกแทนด้วยเลข X (รายได้/ภาษีขาย/การรับชำระอยู่ที่ใบแทน) ให้สั่งยกเลิกใบแทนแทน ·
    /// เดิมตอบ "already voided" สำเร็จ (HTTP 200 โกหก — F2 ข้อ 7) · ใบยกเลิกธรรมดา = idempotent เหมือนเดิม · ยังไม่ยกเลิก = ทำต่อ · G6: pure
    /// </summary>
    /// <param name="liveReplacementNumber">เลขที่ของใบแทน<b>ที่ยังมีผล</b>ปลายสายการแทน (null = ไม่มี/ถูกยกเลิกไปด้วย)</param>
    public static IntegrationVoidOutcome IntegrationVoid(DocumentStatus status, string? documentNumber, string? liveReplacementNumber,
        Guid? liveReplacementId)
    {
        if (status != DocumentStatus.Voided)
            return new IntegrationVoidOutcome(IntegrationVoidKind.Proceed, null);
        if (!string.IsNullOrWhiteSpace(liveReplacementNumber))
            return new IntegrationVoidOutcome(IntegrationVoidKind.Replaced,
                $"เอกสาร {documentNumber} ถูกยกเลิกและออกใบแทนเป็น {liveReplacementNumber} แล้ว (DocumentId ของใบแทน {liveReplacementId}) — "
                + "รายได้ ภาษีขาย และการรับชำระอยู่ที่ใบแทน · ถ้าต้องการยกเลิกการขายนี้ ให้สั่งยกเลิกใบ "
                + $"{liveReplacementNumber} แทน · ระบบยังไม่ได้ยกเลิกอะไร");
        return new IntegrationVoidOutcome(IntegrationVoidKind.AlreadyVoided, "Document already voided");
    }

    private static string Show<T>(T v) => v switch
    {
        null => "(ว่าง)",
        decimal m => m.ToString("N2", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "(ว่าง)",
    };
}

/// <summary>ผลของคำสั่งยกเลิกจากทางเข้าภายนอก (<see cref="SettlementPaidReissue.IntegrationVoid"/>)</summary>
public enum IntegrationVoidKind
{
    /// <summary>ยังไม่ยกเลิก — ยกเลิกตามปกติ</summary>
    Proceed = 0,
    /// <summary>ยกเลิกไปแล้ว (ไม่มีใบแทนที่มีผล) — idempotent</summary>
    AlreadyVoided = 1,
    /// <summary>ถูกยกเลิกและออกใบแทนที่ยังมีผล — ปฏิเสธพร้อมเลขใบแทน</summary>
    Replaced = 2,
}

public sealed record IntegrationVoidOutcome(IntegrationVoidKind Kind, string? Message);
