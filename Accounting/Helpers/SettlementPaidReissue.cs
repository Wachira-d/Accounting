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

/// <summary>ใบเสร็จอัตโนมัติ 1 ใบของการรับชำระที่จะย้าย — สถานะ e-Tax ที่ไปไกลที่สุด (<see cref="DocumentVoidPreconditions.StrongestEtax"/>)</summary>
public sealed record ReissueReceiptFact(string? Number, EtaxStatus? StrongestEtax);

/// <summary>ข้อเท็จจริงของใบเดิม (โหลดจากฐาน · tenant แล้ว) สำหรับ <see cref="SettlementPaidReissue.Decide"/></summary>
/// <param name="PostedBatchPaymentBlock">ผลของ <see cref="SettlementArtifactGuard.CheckDocumentPaymentsAsync"/> — null = ไม่มีการรับชำระจากรอบโอนที่ลงบัญชีแล้ว
/// (ใช้ "ยกเลิกเอกสาร" ตามปกติได้ ทางนี้ไม่เกี่ยว)</param>
/// <param name="CreatedBySettlementBatch">ใบของรอบโอนเอง (ใบสรุป/ใบค่าธรรมเนียม · ป้าย CreatedBy) — ทางที่ถูกคือยกเลิกการลงบัญชี</param>
/// <param name="EtaxAccepted">e-Tax ของใบนี้ได้รับตอบรับแล้ว (ด่านเดียวกับ VoidDocumentAsync)</param>
/// <param name="FilingLocked">อยู่ในรายงานภาษีที่ล็อกการยื่นแล้ว (ด่านเดียวกับ VoidDocumentAsync)</param>
/// <param name="WhtFiledBlock">50 ทวิ ของใบที่อยู่ในแบบที่ยื่นแล้ว (<see cref="WhtCertVoidGuard"/>)</param>
/// <param name="ChildBlock">เอกสารลูก/ใบลดหนี้อ้าง (<see cref="DocumentVoidPreconditions.ChildBlocksAsync"/> — ไม่นับใบเสร็จอัตโนมัติของการรับชำระที่ย้าย)</param>
/// <param name="ClosedPeriodName">งวดบัญชีของวันที่เอกสารที่ปิดแล้ว (null = เปิด/ไม่มีงวด)</param>
/// <param name="SharedPaymentNumber">การรับชำระที่จัดสรรเข้าหลายใบ (ย้ายบางส่วนไม่ได้) — null = ไม่มี</param>
public sealed record SettlementPaidReissueFacts(
    DocumentType Type, DocumentStatus Status, bool IsSettlementReceipt, bool AlreadyReplaced, bool CreatedBySettlementBatch,
    string? PostedBatchPaymentBlock, bool IsDeposit, bool HasDepositApplied,
    bool EtaxAccepted, bool FilingLocked, string? WhtFiledBlock, string? ChildBlock, string? ClosedPeriodName,
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
        if (f.EtaxAccepted)
            return SettlementPaidReissueVerdict.Blocked(
                "e-Tax ของใบนี้ได้รับตอบรับจากกรมสรรพากรแล้ว (Accepted) — ต้องยกเลิก/ออกแทนที่ระบบ e-Tax ของกรมสรรพากรก่อน "
                + "(ระบบนี้ยังไม่มีช่องทางส่งใบแทน e-Tax · ด่านเดียวกับ “ยกเลิกเอกสาร”)" + Tail, "REISSUE-ETAX-ACCEPTED");
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
            var d = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(r.StrongestEtax, r.Number, PaymentVoidCause.User);
            if (d.Action != AutoReceiptEtaxAction.Void)
                return SettlementPaidReissueVerdict.Blocked(
                    $"ใบเสร็จ {r.Number} ที่ออกคู่การรับชำระต้องถูกยกเลิกและออกใหม่อ้างใบใหม่ แต่ใบเสร็จนั้นส่ง e-Tax ไปกรมสรรพากรแล้ว — "
                    + (r.StrongestEtax == EtaxStatus.Accepted
                        ? "ต้องยกเลิก/ออกแทนที่ระบบ e-Tax ของกรมสรรพากรก่อน"
                        : "เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบเสร็จนั้นก่อน (ทำได้ก่อนกรมสรรพากรตอบรับ) แล้วกดอีกครั้ง") + Tail,
                    "REISSUE-RECEIPT-ETAX");
        }
        return SettlementPaidReissueVerdict.Ok;
    }

    /// <summary>
    /// **ด่าน "ใบใหม่เท่าใบเดิม"** — คืนรายการสิ่งที่ต่างซึ่ง<b>ห้ามต่าง</b> (ว่าง = ผ่าน) · ต่างได้เฉพาะผู้ซื้อ (<c>ContactId</c>) · หมายเหตุ ·
    /// คำบรรยายรายบรรทัด · ยอด/บรรทัด/อัตรา VAT/วันที่/tax point ต่าง = ต้องใช้ใบลดหนี้/ใบเพิ่มหนี้ (คำตัดสินข้อ 9) · G6: pure
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
        => $"ยกเลิกและออกฉบับใหม่แทนฉบับเดิม เลขที่ “{originalNumber}” ลงวันที่ {ThaiDate.ToThaiDisplayString(originalDate)} · เหตุที่ยกเลิก: {reason.Trim()}";

    /// <summary>หมายเหตุพิมพ์บนใบใหม่ = หมายเหตุของผู้ใช้ (หรือของใบเดิม) + บรรทัดอ้างใบเดิม (ไม่ซ้ำถ้ามีอยู่แล้ว)</summary>
    public static string ComposeNotes(string? notes, string originalNumber, DateTime originalDate, string reason)
    {
        var note = ReplacementNote(originalNumber, originalDate, reason);
        var baseNotes = notes?.Trim();
        if (string.IsNullOrEmpty(baseNotes)) return note;
        return baseNotes.Contains(note, StringComparison.Ordinal) ? baseNotes : baseNotes + " · " + note;
    }

    /// <summary>หมายเหตุภายในบนใบเดิม (ไม่พิมพ์)</summary>
    public static string OriginalNote(string replacementNumber, string reason, int movedPayments)
        => $"[VOID-REISSUE] ยกเลิกและออกใบแทน “{replacementNumber}” — ย้ายการรับชำระ {movedPayments} รายการ รายการบัญชี และคู่จับของรอบโอนไปใบใหม่ "
           + $"(ยอด/อัตรา/วันที่เท่าเดิม · เงินไม่ถูกกลับรายการ) · เหตุผล: {reason}";

    /// <summary>หมายเหตุภายในบนใบใหม่ (ไม่พิมพ์)</summary>
    public static string ReplacementInternalNote(string originalNumber, string reason, IReadOnlyList<string> changed)
        => $"[VOID-REISSUE] ออกแทน “{originalNumber}” (ยกเลิกแล้ว) — รับการรับชำระ รายการบัญชี และคู่จับของรอบโอนมาจากใบเดิม · เปลี่ยน: "
           + (changed.Count == 0 ? "ไม่มี (ข้อมูลเดิม)" : string.Join(", ", changed)) + $" · เหตุผล: {reason}";

    /// <summary>
    /// คัดลอกทุกช่องที่เป็นค่า (value type · enum · <see cref="Nullable{T}"/> · string) ที่อ่าน/เขียนได้ จาก <paramref name="from"/> ไป <paramref name="to"/> —
    /// ไม่แตะ navigation/คอลเลกชัน · ใช้โคลนเอกสารให้ "ใบใหม่เท่าใบเดิม" โดยไม่ต้องจำรายชื่อช่องเอง (ช่องใหม่ที่เพิ่มทีหลังตามมาเอง) ·
    /// ผู้เรียกต้องตั้งค่าที่ห้ามตามมาเองหลังคัดลอก (Id · เลขที่ · โทเคน ฯลฯ)
    /// </summary>
    public static void CopyScalars<T>(T from, T to) where T : class
    {
        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            if (p.GetSetMethod() == null) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (!(t.IsValueType || t == typeof(string))) continue;
            p.SetValue(to, p.GetValue(from));
        }
    }

    private static string Show<T>(T v) => v switch
    {
        null => "(ว่าง)",
        decimal m => m.ToString("N2", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "(ว่าง)",
    };
}
